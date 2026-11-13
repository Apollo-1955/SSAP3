namespace SSAP3.Core.Connection;

public enum DeviceLink
{
    NotPaired,
    PairedDisconnected,
    Connected,
}

/// <summary>Why the AACP control channel cannot be used at all (no retry helps).</summary>
public enum ControlUnavailableReason
{
    None,

    /// <summary>The SSAP3 profile driver is not installed (normal on production PCs until it is signed).</summary>
    DriverNotInstalled,

    /// <summary>Windows did not expose the Apple AAP service for this device.</summary>
    ServiceNotExposed,

    /// <summary>The driver interface exists but reports an incompatible protocol version.</summary>
    IncompatibleDriver,
}

public enum ControlChannelState
{
    /// <summary>AirPods are not connected, so there is nothing to open.</summary>
    Idle,

    /// <summary>A permanent condition prevents the channel (see the reason).</summary>
    Unavailable,

    /// <summary>One open attempt is in flight.</summary>
    Connecting,

    Ready,

    /// <summary>Waiting for the backoff delay before the next attempt.</summary>
    WaitingToRetry,

    /// <summary>Retry budget exhausted. Waits for a device, system or user event.</summary>
    Failed,

    /// <summary>The system is going to sleep; sessions are closed.</summary>
    Suspended,
}

public enum SupervisorAction
{
    None,
    OpenSession,
    CloseSession,
}

/// <summary>Exponential backoff with full jitter and a bounded retry budget (no infinite loops).</summary>
public sealed record ReconnectPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    public double Multiplier { get; init; } = 2.0;

    /// <summary>Fraction of the delay randomised (0 = none, 0.2 = plus or minus 20 %).</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>Attempts per episode before giving up until an external event.</summary>
    public int MaximumAttempts { get; init; } = 8;

    public TimeSpan DelayFor(int attempt, double unitRandom)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var baseMs = Math.Min(MaximumDelay.TotalMilliseconds, InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, exponent));
        var jitter = 1.0 + (Jitter * ((2.0 * Math.Clamp(unitRandom, 0, 1)) - 1.0));
        return TimeSpan.FromMilliseconds(Math.Clamp(baseMs * jitter, 0, MaximumDelay.TotalMilliseconds));
    }
}

/// <summary>
/// Single-flight state machine for the AACP control session. It decides
/// when to open or close a session and enforces backoff, so there are never
/// two concurrent sessions or open attempts. It performs no I/O: the caller
/// executes the returned actions and reports their results.
/// </summary>
public sealed class ControlChannelSupervisor
{
    private readonly ReconnectPolicy _policy;
    private readonly Func<double> _random;
    private TimeSpan _retryAt;

    public ControlChannelSupervisor(ReconnectPolicy? policy = null, Func<double>? random = null)
    {
        _policy = policy ?? new ReconnectPolicy();
        _random = random ?? Random.Shared.NextDouble;
    }

    public ControlChannelState State { get; private set; } = ControlChannelState.Idle;

    public DeviceLink Link { get; private set; } = DeviceLink.NotPaired;

    public ControlUnavailableReason UnavailableReason { get; private set; }

    public int Attempt { get; private set; }

    public string? LastError { get; private set; }

    public TimeSpan? NextRetryAt => State == ControlChannelState.WaitingToRetry ? _retryAt : null;

    public SupervisorAction OnLinkChanged(DeviceLink link, TimeSpan now)
    {
        Link = link;
        if (link != DeviceLink.Connected)
        {
            var hadSession = State is ControlChannelState.Ready or ControlChannelState.Connecting;
            State = ControlChannelState.Idle;
            Attempt = 0;
            return hadSession ? SupervisorAction.CloseSession : SupervisorAction.None;
        }

        // A (re)connection is a fresh episode: reset the retry budget.
        if (State is ControlChannelState.Idle or ControlChannelState.Failed or ControlChannelState.WaitingToRetry)
        {
            Attempt = 0;
            return Evaluate(now);
        }

        return SupervisorAction.None;
    }

    public SupervisorAction OnAvailabilityChanged(ControlUnavailableReason reason, TimeSpan now)
    {
        UnavailableReason = reason;
        if (reason != ControlUnavailableReason.None)
        {
            var hadSession = State is ControlChannelState.Ready or ControlChannelState.Connecting;
            State = ControlChannelState.Unavailable;
            return hadSession ? SupervisorAction.CloseSession : SupervisorAction.None;
        }

        if (State == ControlChannelState.Unavailable)
        {
            State = ControlChannelState.Idle;
            Attempt = 0;
        }

        return Evaluate(now);
    }

    public void OnSessionOpened()
    {
        if (State == ControlChannelState.Connecting)
        {
            State = ControlChannelState.Ready;
            Attempt = 0;
            LastError = null;
        }
    }

    public SupervisorAction OnSessionFailed(string error, TimeSpan now)
    {
        LastError = error;
        if (State != ControlChannelState.Connecting)
        {
            return SupervisorAction.None;
        }

        return ScheduleRetry(now);
    }

    /// <summary>An established session dropped (link loss, stale session, adapter reset).</summary>
    public SupervisorAction OnSessionLost(string reason, TimeSpan now)
    {
        LastError = reason;
        if (State != ControlChannelState.Ready)
        {
            return SupervisorAction.None;
        }

        Attempt = 0;
        return ScheduleRetry(now);
    }

    public SupervisorAction OnSuspend()
    {
        var hadSession = State is ControlChannelState.Ready or ControlChannelState.Connecting;
        State = ControlChannelState.Suspended;
        return hadSession ? SupervisorAction.CloseSession : SupervisorAction.None;
    }

    public SupervisorAction OnResume(TimeSpan now)
    {
        if (State != ControlChannelState.Suspended)
        {
            return SupervisorAction.None;
        }

        State = ControlChannelState.Idle;
        Attempt = 0;
        return Evaluate(now);
    }

    /// <summary>Explicit user request ("Reconnect"): starts a new episode immediately.</summary>
    public SupervisorAction OnUserRetry(TimeSpan now)
    {
        if (State is ControlChannelState.Connecting or ControlChannelState.Ready or ControlChannelState.Suspended)
        {
            return SupervisorAction.None;
        }

        if (State == ControlChannelState.Unavailable)
        {
            return SupervisorAction.None;
        }

        State = ControlChannelState.Idle;
        Attempt = 0;
        return Evaluate(now);
    }

    /// <summary>Periodic tick; opens a session when a retry is due.</summary>
    public SupervisorAction Tick(TimeSpan now) =>
        State == ControlChannelState.WaitingToRetry && now >= _retryAt
            ? BeginAttempt()
            : SupervisorAction.None;

    private SupervisorAction Evaluate(TimeSpan now)
    {
        if (Link != DeviceLink.Connected ||
            UnavailableReason != ControlUnavailableReason.None ||
            State is ControlChannelState.Connecting or ControlChannelState.Ready or ControlChannelState.Suspended)
        {
            return SupervisorAction.None;
        }

        if (State == ControlChannelState.WaitingToRetry)
        {
            return Tick(now);
        }

        return BeginAttempt();
    }

    private SupervisorAction BeginAttempt()
    {
        Attempt++;
        State = ControlChannelState.Connecting;
        return SupervisorAction.OpenSession;
    }

    private SupervisorAction ScheduleRetry(TimeSpan now)
    {
        if (Attempt >= _policy.MaximumAttempts)
        {
            State = ControlChannelState.Failed;
            return SupervisorAction.None;
        }

        State = ControlChannelState.WaitingToRetry;
        _retryAt = now + _policy.DelayFor(Math.Max(1, Attempt), _random());
        return SupervisorAction.None;
    }
}
