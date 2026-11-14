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
using SSAP3.Core.Motion.Companion;
using SSAP3.Core.Spatial;
using SSAP3.Platform.Windows;

namespace SSAP3.App.Services;

/// <summary>Which motion provider Tracked mode uses, and how to reach a companion.</summary>
internal sealed record MotionSourceSelection(MotionSourceKind Kind, string? CompanionHost, int CompanionPort);

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
    bool DecoderVerified,
    MotionSourceHealth Source,
    TrackingStatus Tracking,
    Vector3? SavedCalibration);

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

    private readonly SynchronizationContext _ui;
    private readonly ConcurrentQueue<Action<TimeSpan>> _commands = new();
    private readonly ControlChannelSupervisor _supervisor = new();
    private readonly ConversationModeController _conversation;
    private readonly AirPodsBatteryPacketParser _batteryParser = new();
    private readonly HeadPoseStateWriter _stateWriter = new();
    private readonly ListeningModeController _listeningModes;
    private readonly LatencyStatistics _driverLatency = new();
    private readonly object _clientSync = new();
    private readonly Thread _thread;
    private volatile bool _stopping;

    private readonly List<OrientationSample> _samples = new(64);
    private Ssap3DriverAacpClient? _client;
    private HeadPoseEstimator _estimator;
    private AirPodsSensorMotionSource _sensorSource;
    private IMotionSource _source;
    private MotionSourceSelection _selection;
    private bool _invertYaw;
    private bool _invertPitch;
    private Vector3? _savedCalibration;
    private string? _address;
    private string? _deviceName;
    private bool _driverPresent;
    private BluetoothRadioState _radio = BluetoothRadioState.Unknown;
    private SpatialEngineMode _spatialMode;
    private SoundstageSettings _stage;
    private bool _headTrackingEnabled = true;
    private bool _motionRequested;
    private TimeSpan _lastLinkPoll = TimeSpan.Zero;
    private TimeSpan _lastDriverProbe = TimeSpan.Zero;
    private TimeSpan _lastStateWrite = TimeSpan.Zero;
    private TimeSpan _lastUiPublish = TimeSpan.Zero;
    private AirPodsBatteryUpdate? _aacpBattery;
    private DateTimeOffset? _aacpBatteryAt;

    public AirPodsService(Ssap3UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _spatialMode = settings.SpatialMode;
        _stage = settings.Soundstage.Sanitized();
        _invertYaw = settings.InvertYaw;
        _invertPitch = settings.InvertPitch;
        _savedCalibration = settings.LateralAxisVector;
        _selection = new MotionSourceSelection(settings.MotionSource, settings.CompanionHost, settings.CompanionPort);
        _sensorSource = new AirPodsSensorMotionSource(_invertYaw, _invertPitch, _savedCalibration);
        _source = CreateSource(_selection);
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

    public void SetCalibration(bool invertYaw, bool invertPitch, bool driftCorrection) => Enqueue(now =>
    {
        _estimator = new HeadPoseEstimator(new HeadPoseEstimatorOptions { DriftCorrection = driftCorrection });
        if (invertYaw != _invertYaw || invertPitch != _invertPitch)
        {
            _invertYaw = invertYaw;
            _invertPitch = invertPitch;
            _sensorSource.Configure(invertYaw, invertPitch);
            ReplaceSource(_selection, now);
        }
    });

    /// <summary>Switches the motion provider without restarting the audio engine.</summary>
    public void SetMotionSource(MotionSourceSelection selection) => Enqueue(now => ReplaceSource(selection, now));

    /// <summary>Starts the guided nod calibration for pitch and roll on the active provider.</summary>
    public void BeginPitchRollCalibration() => Enqueue(_ => _source.BeginCalibration());

    /// <summary>Forgets the saved pitch/roll calibration.</summary>
    public void ClearPitchRollCalibration() => Enqueue(now =>
    {
        _savedCalibration = null;
        _sensorSource.Dispose();
        _sensorSource = new AirPodsSensorMotionSource(_invertYaw, _invertPitch, null);
        if (_channelSend is { } send)
        {
            _sensorSource.AttachChannel(send);
        }

        ReplaceSource(_selection, now);
    });

    private Action<byte[]>? _channelSend;

    private IMotionSource CreateSource(MotionSourceSelection selection)
    {
        switch (selection.Kind)
        {
            case MotionSourceKind.Simulated:
                return new SimulatedMotionSource(new SimulatedMotionOptions(), _invertYaw, _invertPitch, _savedCalibration);
            case MotionSourceKind.Companion:
                var key = CompanionPairingStore.Load();
                if (string.IsNullOrWhiteSpace(selection.CompanionHost) || key is null)
                {
                    return new UnavailableMotionSource(
                        MotionSourceKind.Companion,
                        "No companion is paired. Create a pairing code and enter the companion's address in Settings > Head tracking.");
                }

                try
                {
                    return new CompanionMotionSource(selection.CompanionHost, selection.CompanionPort, key, () => MonotonicClock.Now, _invertYaw, _invertPitch, _savedCalibration);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
                }

            default:
                return _sensorSource;
        }
    }

    private void ReplaceSource(MotionSourceSelection selection, TimeSpan now)
    {
        var wasRunning = _motionRequested;
        if (wasRunning)
        {
            _source.Stop();
        }

        if (!ReferenceEquals(_source, _sensorSource))
        {
            _source.Dispose();
        }

        _selection = selection;
        _source = CreateSource(selection);
        Ssap3AppLog.Write($"Motion provider: {_source.Kind}{(_source.IsSimulated ? " (simulated)" : string.Empty)}.");
        if (wasRunning)
        {
            _estimator.Reset();
            _source.Start(now);
        }
    }

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

        if (!ReferenceEquals(_source, _sensorSource))
        {
            _source.Dispose();
        }

        _sensorSource.Dispose();
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
                    _channelSend = packet => _client?.Send(packet);
                    _sensorSource.AttachChannel(_channelSend);
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
        // RTBuddy sensor channel (opcode 0x17): announcements and motion reports.
        var discovered = _sensorSource.DiscoveredService;
        if (_sensorSource.OnPacket(data, receivedAt))
        {
            if (_sensorSource.DiscoveredService != discovered)
            {
                Ssap3AppLog.Write($"AirPods announced the fused-motion service as ID {_sensorSource.DiscoveredService}.");
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
        var wanted = _spatialMode == SpatialEngineMode.Tracked && _headTrackingEnabled;
        if (!wanted)
        {
            if (_motionRequested)
            {
                StopMotion();
            }

            return;
        }

        if (!_motionRequested)
        {
            _motionRequested = true;
            _estimator.Reset();
            _source.Start(now);
        }

        // Whichever provider is active delivers head-frame samples; the estimator does the rest.
        _samples.Clear();
        _source.Poll(now, _samples);
        foreach (var sample in _samples)
        {
            _estimator.AddSample(sample);
        }

        if (_source.SavedCalibration is { } axis && axis != _savedCalibration)
        {
            _savedCalibration = axis;
            Ssap3AppLog.Write("Pitch/roll calibration saved.");
        }
    }

    private void StopMotion()
    {
        if (!_motionRequested)
        {
            return;
        }

        _motionRequested = false;
        _source.Stop();
    }

    /// <summary>Why the AirPods' own sensors cannot be used right now (shown when that provider is active).</summary>
    private string DescribeSensorUnavailability() =>
        _radio == BluetoothRadioState.Off ? "Bluetooth is off."
        : _supervisor.Link != DeviceLink.Connected ? "The AirPods are not connected."
        : !_driverPresent ? "The SSAP3 AAP driver is not installed. Windows only loads it once Microsoft has signed it (driver/SIGNING.md); use a companion device until then."
        : "The AAP control channel is not open yet.";

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
        if (_channelSend is null)
        {
            _sensorSource.DetachChannel(DescribeSensorUnavailability());
        }

        var health = _source.GetHealth(now);
        var pose = _estimator.GetPose(now);
        var sensorHealth = ReferenceEquals(_source, _sensorSource) ? health : _sensorSource.GetHealth(now);
        var motionStatus = ToStreamStatus(_motionRequested && ReferenceEquals(_source, _sensorSource) ? sensorHealth.State : MotionSourceState.Stopped);
        var snapshot = new ServiceSnapshot(
            DeviceStatus.From(_supervisor, motionStatus, sensorHealth.RateHz, _radio == BluetoothRadioState.Off),
            _deviceName,
            _address,
            _driverPresent,
            pose,
            ToStreamStatus(_motionRequested ? health.State : MotionSourceState.Stopped),
            health.RateHz,
            health.SamplesReceived,
            _estimator.RejectedSampleCount,
            _listeningModes.ReportedMode,
            _aacpBattery,
            _aacpBatteryAt,
            _conversation.Phase,
            _spatialMode,
            HeadPoseStateWriter.IsEngineInstalled && _stateWriter.IsAvailable,
            _stateWriter.LastError,
            _driverLatency.Summarize(),
            health.Description,
            DecoderVerified: false,
            health,
            TrackingStatusEvaluator.Evaluate(_spatialMode, _headTrackingEnabled, _motionRequested ? health : null, pose),
            _savedCalibration);
        _ui.Post(_ => Updated?.Invoke(this, snapshot), null);
    }

    private static MotionStreamStatus ToStreamStatus(MotionSourceState state) => state switch
    {
        MotionSourceState.Streaming => MotionStreamStatus.Streaming,
        MotionSourceState.Stalled or MotionSourceState.Reconnecting => MotionStreamStatus.Stalled,
        MotionSourceState.Failed => MotionStreamStatus.Unsupported,
        MotionSourceState.Connecting or MotionSourceState.WaitingForData or MotionSourceState.Unavailable => MotionStreamStatus.WaitingForFirstFrame,
        _ => MotionStreamStatus.Inactive,
    };

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

        _channelSend = null;
        _sensorSource.DetachChannel(DescribeSensorUnavailability());
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
