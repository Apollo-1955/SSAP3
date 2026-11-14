using System.Numerics;

namespace SSAP3.Core.Motion;

public enum MotionSourceKind
{
    /// <summary>The AirPods' own motion sensors over the AAP channel (needs SSAP3's driver).</summary>
    AirPodsSensors,

    /// <summary>A paired companion device that reads the AirPods and forwards orientation.</summary>
    Companion,

    /// <summary>Generated head motion for development and testing. Never real tracking.</summary>
    Simulated,
}

public enum MotionSourceState
{
    Stopped,

    /// <summary>The source cannot run (no driver, not configured, and so on); see the problem text.</summary>
    Unavailable,

    Connecting,

    /// <summary>Connected and requested, no measurement yet.</summary>
    WaitingForData,

    Streaming,

    /// <summary>Measurements stopped arriving; the source is trying to recover.</summary>
    Stalled,

    Reconnecting,

    /// <summary>The source gave up (for example the device never streamed); it keeps retrying slowly.</summary>
    Failed,
}

/// <summary>What a motion source reports about itself, for the tracking manager and diagnostics.</summary>
public sealed record MotionSourceHealth(
    MotionSourceKind Kind,
    MotionSourceState State,
    bool IsSimulated,
    string Description,
    string? Problem,
    double RateHz,
    TimeSpan? LastSampleAge,
    long SamplesReceived,
    long SamplesRejected,
    bool ProvidesPitchRoll,
    HeadAxisCalibrationState Calibration,
    double? TransportDelayMs);

/// <summary>
/// One provider of head orientation. Every provider delivers samples in SSAP3's
/// head frame (+X right, +Y forward, +Z up), relative to the provider's own
/// reference, with monotonic timestamps. The estimator and the audio engine never
/// need to know which provider is active. Calls come from one thread (the
/// motion thread); implementations may use their own I/O threads internally.
/// </summary>
public interface IMotionSource : IDisposable
{
    MotionSourceKind Kind { get; }

    bool IsSimulated { get; }

    void Start(TimeSpan now);

    void Stop();

    /// <summary>Appends the samples received since the last call.</summary>
    void Poll(TimeSpan now, ICollection<OrientationSample> samples);

    MotionSourceHealth GetHealth(TimeSpan now);

    /// <summary>Starts the pitch/roll calibration if the source supports it.</summary>
    bool BeginCalibration();

    /// <summary>The calibrated lateral axis to persist, if the source has one.</summary>
    Vector3? SavedCalibration { get; }
}

/// <summary>
/// The AirPods' own sensors over the AAP channel. The caller owns the AACP
/// session: it attaches a send delegate when the channel is ready, forwards
/// received packets to <see cref="OnPacket"/>, and detaches with a reason when
/// the channel goes away.
/// </summary>
public sealed class AirPodsSensorMotionSource : IMotionSource
{
    private const int MaximumPending = 256;
    private static readonly TimeSpan OwnsConnectionSettle = TimeSpan.FromMilliseconds(350);

    private readonly object _sync = new();
    private readonly List<OrientationSample> _pending = [];
    private readonly MotionStreamController _controller = new();
    private readonly MotionStreamMonitor _monitor = new();
    private DeviceMotionOrientationDecoder _decoder;
    private Action<byte[]>? _send;
    private string? _unavailableReason = "The AAP channel is not open.";
    private bool _running;
    private bool _ownsConnectionSent;
    private TimeSpan _requestedAt;
    private TimeSpan _lastSample;
    private TimeSpan _lastFrameAt;
    private long _rejected;

    public AirPodsSensorMotionSource(bool invertYaw, bool invertPitch, Vector3? savedCalibration)
    {
        _decoder = new DeviceMotionOrientationDecoder(invertYaw, invertPitch, savedCalibration);
    }

    public MotionSourceKind Kind => MotionSourceKind.AirPodsSensors;

    public bool IsSimulated => false;

    public Vector3? SavedCalibration => _decoder.LateralAxisInSensorFrame;

    public int? ActiveService => _controller.ActiveService;

    public int? DiscoveredService => _controller.DiscoveredService;

    public int Reanchors => _decoder.Reanchors;

    /// <summary>The AACP session is ready; commands go through <paramref name="send"/>.</summary>
    public void AttachChannel(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _unavailableReason = null;
        _ownsConnectionSent = false;
        if (_controller.IsRunning)
        {
            _ = _controller.Stop();
        }
    }

    /// <summary>The AACP session went away; <paramref name="reason"/> is shown to the user.</summary>
    public void DetachChannel(string reason)
    {
        _send = null;
        _unavailableReason = reason;
        _ = _controller.Stop();
        _monitor.Stop();
    }

    public void Start(TimeSpan now)
    {
        _running = true;
        _ownsConnectionSent = false;
        _requestedAt = now;
        _lastSample = TimeSpan.Zero;
        _decoder.Reset();
        _monitor.Start(now);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _monitor.Stop();
        foreach (var command in _controller.Stop())
        {
            TrySend(command);
        }
    }

    /// <summary>Handles one received AACP packet; returns true when it belonged to the sensor channel.</summary>
    public bool OnPacket(ReadOnlySpan<byte> data, TimeSpan receivedAt)
    {
        if (!SensorDataWx.TryParse(data, out var message))
        {
            return false;
        }

        if (message.HasAnnouncement)
        {
            _controller.OnAnnouncement(message.AnnouncedService, message.AnnouncementPayload);
        }

        if (!_running || !HeadTrackingFrameParser.TryParse(data, out var frame) || !_controller.AcceptReport(frame.Service))
        {
            return true;
        }

        _monitor.OnFrame(receivedAt);
        _lastFrameAt = receivedAt;
        if (!_decoder.TryDecode(frame, out var orientation))
        {
            _rejected++;
            return true;
        }

        var timestamp = receivedAt > _lastSample ? receivedAt : _lastSample + TimeSpan.FromTicks(2500);
        _lastSample = timestamp;
        lock (_sync)
        {
            if (_pending.Count >= MaximumPending)
            {
                _pending.RemoveAt(0);
            }

            _pending.Add(new OrientationSample(timestamp, orientation, _decoder.ProvidesPitchRoll));
        }

        return true;
    }

    public void Poll(TimeSpan now, ICollection<OrientationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (_running && _send is not null)
        {
            if (!_ownsConnectionSent)
            {
                TrySend(AacpPacketFactory.CreateOwnsConnection(true));
                _ownsConnectionSent = true;
                _requestedAt = now;
                _monitor.Start(now);
            }
            else if (now - _requestedAt >= OwnsConnectionSettle)
            {
                var commands = _controller.IsRunning
                    ? _controller.Tick(now, _monitor.Evaluate(now))
                    : _controller.Start(now);
                foreach (var command in commands)
                {
                    TrySend(command);
                }
            }
        }

        lock (_sync)
        {
            foreach (var sample in _pending)
            {
                samples.Add(sample);
            }

            _pending.Clear();
        }
    }

    public bool BeginCalibration()
    {
        _decoder.BeginCalibration();
        return true;
    }

    public MotionSourceHealth GetHealth(TimeSpan now)
    {
        var status = _monitor.Evaluate(now);
        var state = !_running ? MotionSourceState.Stopped
            : _send is null ? MotionSourceState.Unavailable
            : status switch
            {
                MotionStreamStatus.Streaming => MotionSourceState.Streaming,
                MotionStreamStatus.Stalled => MotionSourceState.Stalled,
                MotionStreamStatus.Unsupported => MotionSourceState.Failed,
                _ => MotionSourceState.WaitingForData,
            };
        var problem = state switch
        {
            MotionSourceState.Unavailable => _unavailableReason,
            MotionSourceState.Failed => "The AirPods did not stream motion after every known start command. SSAP3 keeps retrying.",
            MotionSourceState.Stalled => "Motion reports stopped; restarting the stream.",
            _ => null,
        };
        return new MotionSourceHealth(
            Kind,
            state,
            IsSimulated: false,
            Description: _controller.ActiveService is { } service
                ? $"AirPods motion sensors (AAP service {service})"
                : "AirPods motion sensors (AAP driver)",
            problem,
            _monitor.FrameRateHz,
            _monitor.FramesReceived > 0 ? now - _lastFrameAt : null,
            _monitor.FramesReceived,
            _rejected,
            _decoder.ProvidesPitchRoll,
            _decoder.CalibrationState,
            TransportDelayMs: null);
    }

    /// <summary>Replaces the axis inversion settings, keeping the calibration.</summary>
    public void Configure(bool invertYaw, bool invertPitch)
    {
        _decoder = new DeviceMotionOrientationDecoder(invertYaw, invertPitch, _decoder.LateralAxisInSensorFrame);
    }

    public void Dispose() => Stop();

    private void TrySend(byte[] command)
    {
        try
        {
            _send?.Invoke(command);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A dying link is reported through DetachChannel by the session owner.
        }
    }
}
