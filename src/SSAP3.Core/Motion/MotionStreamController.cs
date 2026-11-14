namespace SSAP3.Core.Motion;

/// <summary>
/// Decides which SensorDataWX start and stop commands to send. Pure logic with
/// no I/O, so every decision is unit-tested.
///
/// Service choice, in order:
/// <list type="number">
/// <item>A service the AirPods announce as "devmotion6". Some firmware assigns the
/// fused-motion service a dynamic ID; AirPods Max 2 firmware 8E258 uses 6.</item>
/// <item>16 (DEVMOTION6), used by AirPods Pro 2 and AirPods 4.</item>
/// <item>14, LibrePods' original start packet.</item>
/// </list>
/// A candidate that produces no report within <see cref="CandidateTimeout"/> is
/// followed by the next one. The first service whose reports arrive becomes the
/// active service; reports from any other service are ignored. A stalled active
/// stream is restarted. Stopping sends a stop command to every service started
/// in this session.
/// </summary>
public sealed class MotionStreamController
{
    public static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StallRestartInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RetryCycleAfter = TimeSpan.FromSeconds(6);

    private readonly List<int> _started = [];
    private readonly List<int> _triedThisCycle = [];
    private int _requestId;
    private bool _running;
    private TimeSpan _lastStart;
    private TimeSpan _cycleStart;

    public MotionStreamController(int firstRequestId = 0x40)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstRequestId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstRequestId, SensorDataWx.MaximumRequestId);
        _requestId = firstRequestId - 1;
    }

    public uint IntervalMicroseconds { get; init; } = SensorDataWx.DefaultIntervalMicroseconds;

    /// <summary>Service announced as "devmotion6", if any.</summary>
    public int? DiscoveredService { get; private set; }

    /// <summary>The service whose reports are being used.</summary>
    public int? ActiveService { get; private set; }

    public bool IsRunning => _running;

    public IReadOnlyList<int> StartedServices => _started;

    /// <summary>Records a service announcement; only "devmotion6" descriptors matter.</summary>
    public void OnAnnouncement(int service, ReadOnlySpan<byte> descriptor)
    {
        if (service is >= 1 and <= 127 && SensorDataWx.AnnouncesDeviceMotion(descriptor))
        {
            DiscoveredService = service;
        }
    }

    /// <summary>Returns true when a motion report from <paramref name="service"/> should be used.</summary>
    public bool AcceptReport(int service)
    {
        if (!_running)
        {
            return false;
        }

        if (ActiveService is null && _started.Contains(service))
        {
            ActiveService = service;
        }

        return ActiveService == service;
    }

    /// <summary>Begins a session and returns the first start command.</summary>
    public IReadOnlyList<byte[]> Start(TimeSpan now)
    {
        _running = true;
        ActiveService = null;
        _started.Clear();
        _triedThisCycle.Clear();
        _cycleStart = now;
        return StartNextCandidate(now);
    }

    /// <summary>Returns commands due now: the next candidate, or a restart of a stalled stream.</summary>
    public IReadOnlyList<byte[]> Tick(TimeSpan now, MotionStreamStatus status)
    {
        if (!_running)
        {
            return [];
        }

        if (ActiveService is { } active)
        {
            if (status == MotionStreamStatus.Stalled && now - _lastStart >= StallRestartInterval)
            {
                _lastStart = now;
                return [Command(active, IntervalMicroseconds)];
            }

            return [];
        }

        // A newly discovered devmotion6 service is tried at once.
        if (DiscoveredService is { } discovered && !_triedThisCycle.Contains(discovered))
        {
            return StartService(discovered, now);
        }

        if (now - _lastStart < CandidateTimeout)
        {
            return [];
        }

        var next = StartNextCandidate(now);
        if (next.Count > 0)
        {
            return next;
        }

        // Every candidate was tried: start a new cycle after a pause.
        if (now - _cycleStart >= RetryCycleAfter)
        {
            _triedThisCycle.Clear();
            _cycleStart = now;
            return StartNextCandidate(now);
        }

        return [];
    }

    /// <summary>Ends the session and returns a stop command for every service it started.</summary>
    public IReadOnlyList<byte[]> Stop()
    {
        var commands = _started.Select(service => Command(service, 0)).ToArray();
        _running = false;
        ActiveService = null;
        _started.Clear();
        _triedThisCycle.Clear();
        return commands;
    }

    private IReadOnlyList<byte[]> StartNextCandidate(TimeSpan now)
    {
        foreach (var service in Candidates())
        {
            if (!_triedThisCycle.Contains(service))
            {
                return StartService(service, now);
            }
        }

        return [];
    }

    private IEnumerable<int> Candidates()
    {
        if (DiscoveredService is { } discovered)
        {
            yield return discovered;
        }

        yield return SensorDataWx.ServiceDeviceMotion;
        yield return SensorDataWx.ServiceLegacy;
    }

    private IReadOnlyList<byte[]> StartService(int service, TimeSpan now)
    {
        _triedThisCycle.Add(service);
        if (!_started.Contains(service))
        {
            _started.Add(service);
        }

        _lastStart = now;
        return [Command(service, IntervalMicroseconds)];
    }

    private byte[] Command(int service, uint interval)
    {
        _requestId = _requestId >= SensorDataWx.MaximumRequestId ? 1 : _requestId + 1;
        return SensorDataWx.CreateServiceSetting(_requestId, service, interval);
    }
}
