using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using Microsoft.Win32;
using SSAP3.Core;
using SSAP3.Core.Aacp;
using SSAP3.Core.Connection;
using SSAP3.Core.Conversation;
using SSAP3.Core.Motion;
using SSAP3.Core.Spatial;
using SSAP3.Platform.Windows;

namespace SSAP3.App.Services;

/// <summary>Immutable view of the service for the UI. Every field reflects observed state.</summary>
internal sealed record ServiceSnapshot(
    DeviceStatus Status,
    string? DeviceName,
    string? Address,
    bool DriverPresent,
    HeadPose Pose,
    MotionStreamStatus Motion,
    double MotionRateHz,
    long MotionFrames,
    int RejectedSamples,
    NoiseControlMode? ReportedMode,
    AirPodsBatteryUpdate? AacpBattery,
    DateTimeOffset? AacpBatteryAt,
    ConversationPhase ConversationPhase,
    SpatialEngineMode SpatialMode,
    bool SpatialEngineInstalled,
    string? SpatialEngineError,
    LatencySummary DriverLatency,
    string DecoderName,
    bool DecoderVerified);

/// <summary>
/// SSAP3 Motion and control orchestration. One dedicated thread owns the
/// AACP session, motion pipeline and engine state, so the UI thread never
/// blocks on Bluetooth and the session is never used concurrently by two
/// loops.
/// </summary>
internal sealed class AirPodsService : IDisposable
{
    private static readonly TimeSpan ActivePoll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan LinkPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DriverProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatePublishInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan UiPublishInterval = TimeSpan.FromMilliseconds(66);
    private static readonly TimeSpan OwnsConnectionSettle = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan FallbackStartAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StallRestartAfter = TimeSpan.FromSeconds(4);

    private readonly SynchronizationContext _ui;
    private readonly ConcurrentQueue<Action<TimeSpan>> _commands = new();
    private readonly ControlChannelSupervisor _supervisor = new();
    private readonly MotionStreamMonitor _motion = new();
    private readonly ConversationModeController _conversation;
    private readonly AirPodsBatteryPacketParser _batteryParser = new();
    private readonly HeadPoseStateWriter _stateWriter = new();
    private readonly ListeningModeController _listeningModes;
    private readonly LatencyStatistics _driverLatency = new();
    private readonly object _clientSync = new();
    private readonly Thread _thread;
    private volatile bool _stopping;

    private Ssap3DriverAacpClient? _client;
    private HeadPoseEstimator _estimator;
    private IOrientationDecoder _decoder;
    private string? _address;
    private string? _deviceName;
    private bool _driverPresent;
    private BluetoothRadioState _radio = BluetoothRadioState.Unknown;
    private SpatialEngineMode _spatialMode;
    private SoundstageSettings _stage;
    private bool _headTrackingEnabled = true;
    private bool _motionRequested;
    private bool _ownsConnectionSent;
    private bool _primaryStartSent;
    private bool _fallbackStartSent;
    private TimeSpan _motionRequestedAt;
    private TimeSpan _lastMotionRestart;
    private TimeSpan _lastLinkPoll = TimeSpan.Zero;
    private TimeSpan _lastDriverProbe = TimeSpan.Zero;
    private TimeSpan _lastStateWrite = TimeSpan.Zero;
    private TimeSpan _lastUiPublish = TimeSpan.Zero;
    private TimeSpan _lastSampleTime;
    private AirPodsBatteryUpdate? _aacpBattery;
    private DateTimeOffset? _aacpBatteryAt;

    public AirPodsService(Ssap3UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _spatialMode = settings.SpatialMode;
        _stage = settings.Soundstage.Sanitized();
        _decoder = new LegacyOpaqueOrientationDecoder(settings.InvertYaw, settings.InvertPitch);
        _estimator = new HeadPoseEstimator(new HeadPoseEstimatorOptions { DriftCorrection = settings.DriftCorrection });
        _conversation = new ConversationModeController(new ConversationModeOptions
        {
            SilenceTimeout = TimeSpan.FromSeconds(settings.ConversationSilenceSeconds),
        })
        { Enabled = settings.ConversationAwarenessEnabled };
        _listeningModes = new ListeningModeController(SendPacketAsync);
        _thread = new Thread(Run) { IsBackground = true, Name = "SSAP3 Motion", Priority = ThreadPriority.AboveNormal };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event EventHandler<ServiceSnapshot>? Updated;

    public void Start() => _thread.Start();

    public Task<ListeningModeResult> SetListeningModeAsync(NoiseControlMode mode) =>
        _listeningModes.RequestAsync(mode);

    public void SetSpatialMode(SpatialEngineMode mode) => Enqueue(_ => _spatialMode = mode);

    public void SetSoundstage(SoundstageSettings stage) => Enqueue(_ => _stage = stage.Sanitized());

    public void SetHeadTrackingEnabled(bool enabled) => Enqueue(_ => _headTrackingEnabled = enabled);

    public void Recenter() => Enqueue(_ => _estimator.RequestRecenter());

    public void SetCalibration(bool invertYaw, bool invertPitch, bool driftCorrection) => Enqueue(_ =>
    {
        _decoder = new LegacyOpaqueOrientationDecoder(invertYaw, invertPitch);
        _estimator = new HeadPoseEstimator(new HeadPoseEstimatorOptions { DriftCorrection = driftCorrection });
        _lastSampleTime = TimeSpan.Zero;
    });

    public void SetConversationAwareness(bool enabled) => Enqueue(_ => _conversation.Enabled = enabled);

    public void Reconnect() => Enqueue(now =>
    {
        _lastDriverProbe = TimeSpan.Zero;
        Execute(_supervisor.OnUserRetry(now), now);
    });

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _stopping = true;
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            Ssap3AppLog.Write("Motion thread did not stop within 3 s.");
        }

        _stateWriter.Dispose();
    }

    private void Enqueue(Action<TimeSpan> command) => _commands.Enqueue(command);

    /// <summary>True when an interval has elapsed; a zero "last" time means "never ran".</summary>
    private static bool IsDue(TimeSpan now, TimeSpan last, TimeSpan interval) =>
        last == TimeSpan.Zero || now < last || now - last >= interval;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Enqueue(now => Execute(_supervisor.OnSuspend(), now));
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Enqueue(now =>
            {
                _lastLinkPoll = TimeSpan.Zero;
                _lastDriverProbe = TimeSpan.Zero;
                Execute(_supervisor.OnResume(now), now);
            });
        }
    }

    private void Run()
    {
        using var waiter = new HighResolutionWaiter();
        Ssap3AppLog.Write($"SSAP3 Motion started (high-resolution timer: {waiter.IsHighResolution}, kernel timestamps: {MonotonicClock.UsesInterruptTime}).");
        while (!_stopping)
        {
            var now = MonotonicClock.Now;
            try
            {
                while (_commands.TryDequeue(out var command))
                {
                    command(now);
                }

                PollLink(now);
                ProbeDriver(now);
                Execute(_supervisor.Tick(now), now);
                PumpPackets(now);
                UpdateMotion(now);
                PublishEngineState(now);
                PublishUi(now);
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                Ssap3AppLog.Write($"Motion loop recovered from: {exception.GetType().Name}: {exception.Message}");
                LoseSession(exception.Message, now);
            }
#pragma warning disable CA1031 // Last-resort guard: a bug in one tick must not terminate the tray app.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Ssap3AppLog.Write($"Unexpected motion-loop error (recovering): {exception}");
                LoseSession("Internal error", now);
                Thread.Sleep(500);
            }

            waiter.Wait(_motionRequested ? ActivePoll : IdlePoll);
        }

        StopMotion();
        CloseClient();
        _stateWriter.Write(new SpatialEngineState(_spatialMode, TrackingState.None, Quaternion.Identity, _stage));
        Ssap3AppLog.Write("SSAP3 Motion stopped.");
    }

    private void PollLink(TimeSpan now)
    {
        if (!IsDue(now, _lastLinkPoll, LinkPollInterval))
        {
            return;
        }

        _lastLinkPoll = now;
        _radio = WindowsBluetoothRadio.Query();
        var device = WindowsRememberedAirPodsFinder.FindBest();
        var link = device is null ? DeviceLink.NotPaired : device.Connected ? DeviceLink.Connected : DeviceLink.PairedDisconnected;
        if (device is not null && !string.Equals(device.Address, _address, StringComparison.OrdinalIgnoreCase))
        {
            CloseClient();
            _address = device.Address;
        }

        _deviceName = device?.Name;
        if (link != _supervisor.Link)
        {
            Execute(_supervisor.OnLinkChanged(link, now), now);
        }
    }

    private void ProbeDriver(TimeSpan now)
    {
        if (!IsDue(now, _lastDriverProbe, DriverProbeInterval))
        {
            return;
        }

        _lastDriverProbe = now;
        _driverPresent = Ssap3DriverAacpClient.IsDriverPresent;
        var reason = _driverPresent ? ControlUnavailableReason.None : ControlUnavailableReason.DriverNotInstalled;
        if (reason != _supervisor.UnavailableReason || _supervisor.State == ControlChannelState.Unavailable)
        {
            Execute(_supervisor.OnAvailabilityChanged(reason, now), now);
        }
    }

    private void Execute(SupervisorAction action, TimeSpan now)
    {
        switch (action)
        {
            case SupervisorAction.OpenSession:
                OpenSession(now);
                break;
            case SupervisorAction.CloseSession:
                StopMotion();
                CloseClient();
                break;
        }
    }

    private void OpenSession(TimeSpan now)
    {
        if (string.IsNullOrWhiteSpace(_address))
        {
            Execute(_supervisor.OnSessionFailed("No paired AirPods address.", now), now);
            return;
        }

        var result = Ssap3DriverAacpClient.TryConnect(_address);
        switch (result.Outcome)
        {
            case DriverConnectOutcome.Connected:
                lock (_clientSync)
                {
                    _client = result.Client;
                }

                try
                {
                    _client!.Send(AacpPacketFactory.CreateHandshake());
                    _client.Send(AacpPacketFactory.CreateFeatureFlags());
                    _client.Send(AacpPacketFactory.CreateNotificationRequest());
                    _supervisor.OnSessionOpened();
                    Ssap3AppLog.Write("AACP session initialised.");
                }
                catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
                {
                    CloseClient();
                    Execute(_supervisor.OnSessionFailed(exception.Message, now), now);
                }

                break;
            case DriverConnectOutcome.NotInstalled:
                Execute(_supervisor.OnAvailabilityChanged(ControlUnavailableReason.DriverNotInstalled, now), now);
                break;
            case DriverConnectOutcome.Incompatible:
                Execute(_supervisor.OnAvailabilityChanged(ControlUnavailableReason.IncompatibleDriver, now), now);
                break;
            default:
                Execute(_supervisor.OnSessionFailed(result.Message, now), now);
                break;
        }
    }

    private void PumpPackets(TimeSpan now)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        for (var index = 0; index < 64; index++)
        {
            var packet = client.Receive();
            if (packet.IsEmpty)
            {
                return;
            }

            var receivedAt = MonotonicClock.FromDriverTime(packet.ReceiveInterruptTime);
            if (receivedAt is { } kernelTime && kernelTime <= now)
            {
                _driverLatency.Add((now - kernelTime).TotalMilliseconds);
            }

            HandlePacket(packet.Data, receivedAt ?? now, now);
        }
    }

    private void HandlePacket(byte[] data, TimeSpan receivedAt, TimeSpan now)
    {
        if (HeadTrackingFrameParser.TryParse(data, out var frame))
        {
            _motion.OnFrame(receivedAt);
            if (_decoder.TryDecode(frame, out var orientation))
            {
                var timestamp = receivedAt > _lastSampleTime ? receivedAt : _lastSampleTime + TimeSpan.FromTicks(2500);
                _lastSampleTime = timestamp;
                _estimator.AddSample(new OrientationSample(timestamp, orientation, _decoder.Info.ProvidesRoll));
            }

            return;
        }

        if (!AacpFrame.TryParse(data, out var aacp))
        {
            return;
        }

        if (_listeningModes.OnFrame(aacp))
        {
            if (_listeningModes.ReportedMode is { } mode)
            {
                _conversation.OnModeReported(mode, now);
            }

            return;
        }

        if (ConversationAwarenessLevel.TryParse(aacp, out var level))
        {
            ApplyConversationDecision(_conversation.OnSpeech(level.IndicatesSpeaking, now));
            return;
        }

        if (_batteryParser.TryParse(data, out var battery) && battery is not null)
        {
            _aacpBattery = battery;
            _aacpBatteryAt = DateTimeOffset.UtcNow;
        }
    }

    private void ApplyConversationDecision(ConversationDecision decision)
    {
        if (decision.RequestedMode is not { } mode)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var result = await _listeningModes.RequestAsync(mode).ConfigureAwait(false);
            Ssap3AppLog.Write($"Conversation mode request {mode}: {result.Outcome}.");
            if (result.Outcome != CommandOutcome.Confirmed)
            {
                Enqueue(now => _conversation.OnRequestFailed(now));
            }
        });
    }

    private void UpdateMotion(TimeSpan now)
    {
        ApplyConversationDecision(_conversation.Tick(now));
        var wanted = _spatialMode == SpatialEngineMode.Tracked && _headTrackingEnabled && _supervisor.State == ControlChannelState.Ready;
        if (!wanted)
        {
            if (_motionRequested)
            {
                StopMotion();
            }

            return;
        }

        var client = _client;
        if (client is null)
        {
            return;
        }

        if (!_motionRequested)
        {
            _motionRequested = true;
            _ownsConnectionSent = false;
            _primaryStartSent = false;
            _fallbackStartSent = false;
            _motionRequestedAt = now;
            _lastMotionRestart = now;
            _estimator.Reset();
            _lastSampleTime = TimeSpan.Zero;
            _motion.Start(now);
        }

        if (!_ownsConnectionSent)
        {
            client.Send(AacpPacketFactory.CreateOwnsConnection(true));
            _ownsConnectionSent = true;
            return;
        }

        if (!_primaryStartSent && now - _motionRequestedAt >= OwnsConnectionSettle)
        {
            client.Send(AacpPacketFactory.CreateAlternateStartHeadTracking());
            _primaryStartSent = true;
            return;
        }

        var status = _motion.Evaluate(now);
        if (!_fallbackStartSent && _primaryStartSent && status == MotionStreamStatus.WaitingForFirstFrame &&
            now - _motionRequestedAt >= FallbackStartAfter)
        {
            client.Send(AacpPacketFactory.CreateStartHeadTracking());
            _fallbackStartSent = true;
        }

        // Recovery: re-request a stalled stream, at most every few seconds.
        if (status == MotionStreamStatus.Stalled && now - _lastMotionRestart >= StallRestartAfter)
        {
            _lastMotionRestart = now;
            client.Send(AacpPacketFactory.CreateAlternateStartHeadTracking());
        }
    }

    private void StopMotion()
    {
        if (!_motionRequested)
        {
            return;
        }

        _motionRequested = false;
        _motion.Stop();
        try
        {
            _client?.Send(AacpPacketFactory.CreateAlternateStopHeadTracking());
            _client?.Send(AacpPacketFactory.CreateStopHeadTracking());
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
        {
            // Stopping an optional stream on a dying link is harmless.
        }
    }

    private void PublishEngineState(TimeSpan now)
    {
        if (!IsDue(now, _lastStateWrite, StatePublishInterval))
        {
            return;
        }

        _lastStateWrite = now;
        var pose = _estimator.GetPose(now);
        var tracking = _spatialMode == SpatialEngineMode.Tracked && _motionRequested ? pose.State : TrackingState.None;
        var orientation = tracking == TrackingState.None ? Quaternion.Identity : pose.Orientation;
        _stateWriter.Write(new SpatialEngineState(_spatialMode, tracking, orientation, _stage));
    }

    private void PublishUi(TimeSpan now)
    {
        if (!IsDue(now, _lastUiPublish, UiPublishInterval) || Updated is null)
        {
            return;
        }

        _lastUiPublish = now;
        var motionStatus = _motion.Evaluate(now);
        var snapshot = new ServiceSnapshot(
            DeviceStatus.From(_supervisor, motionStatus, _motion.FrameRateHz, _radio == BluetoothRadioState.Off),
            _deviceName,
            _address,
            _driverPresent,
            _estimator.GetPose(now),
            motionStatus,
            _motion.FrameRateHz,
            _motion.FramesReceived,
            _estimator.RejectedSampleCount,
            _listeningModes.ReportedMode,
            _aacpBattery,
            _aacpBatteryAt,
            _conversation.Phase,
            _spatialMode,
            HeadPoseStateWriter.IsEngineInstalled && _stateWriter.IsAvailable,
            _stateWriter.LastError,
            _driverLatency.Summarize(),
            _decoder.Info.Name,
            _decoder.Info.HardwareVerified);
        _ui.Post(_ => Updated?.Invoke(this, snapshot), null);
    }

    private Task SendPacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_clientSync)
        {
            var client = _client ?? throw new InvalidOperationException("AirPods controls are not connected.");
            client.Send(packet);
        }

        return Task.CompletedTask;
    }

    private void LoseSession(string reason, TimeSpan now)
    {
        var hadClient = _client is not null;
        _motionRequested = false;
        _motion.Stop();
        CloseClient();
        if (hadClient)
        {
            Execute(_supervisor.OnSessionLost(reason, now), now);
        }
    }

    private void CloseClient()
    {
        Ssap3DriverAacpClient? client;
        lock (_clientSync)
        {
            client = _client;
            _client = null;
        }

        client?.Dispose();
    }
}

internal readonly record struct LatencySummary(int Count, double MedianMs, double P95Ms);

/// <summary>Rolling statistics over the last 512 measurements (measured values only).</summary>
internal sealed class LatencyStatistics
{
    private readonly double[] _values = new double[512];
    private readonly object _sync = new();
    private int _count;
    private int _next;

    public void Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 10_000)
        {
            return;
        }

        lock (_sync)
        {
            _values[_next] = milliseconds;
            _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length);
        }
    }

    public LatencySummary Summarize()
    {
        double[] copy;
        lock (_sync)
        {
            copy = _values.Take(_count).ToArray();
        }

        if (copy.Length == 0)
        {
            return default;
        }

        Array.Sort(copy);
        return new LatencySummary(copy.Length, copy[copy.Length / 2], copy[Math.Min(copy.Length - 1, (int)(copy.Length * 0.95))]);
    }
}
