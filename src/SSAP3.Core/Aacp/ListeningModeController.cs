namespace SSAP3.Core.Aacp;

public enum CommandOutcome
{
    /// <summary>The AirPods reported the requested mode after the command.</summary>
    Confirmed,

    /// <summary>The AirPods reported a different mode: the request was not applied.</summary>
    Rejected,

    /// <summary>The command was sent but no report arrived before the timeout.</summary>
    Unconfirmed,

    /// <summary>The command could not be sent.</summary>
    NotSent,
}

public sealed record ListeningModeResult(
    CommandOutcome Outcome,
    NoiseControlMode Requested,
    NoiseControlMode? Reported,
    string Message);

/// <summary>
/// Sends listening-mode commands and reports success only after the AirPods
/// echo the applied mode (<c>04 00 04 00 09 00 0D [mode] 00 00 00</c>), as
/// LibrePods documents. This fixes S-14 (optimistic success) from
/// SECURITY_AUDIT.md.
/// </summary>
public sealed class ListeningModeController
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1.5);

    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private TaskCompletionSource<NoiseControlMode>? _pending;

    public ListeningModeController(Func<byte[], CancellationToken, Task> send, TimeProvider? time = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Last mode the AirPods themselves reported; null until one arrives.</summary>
    public NoiseControlMode? ReportedMode { get; private set; }

    public DateTimeOffset? ReportedAt { get; private set; }

    public event EventHandler<NoiseControlMode>? ModeReported;

    /// <summary>Feeds one received frame. Returns true when it was a listening-mode report.</summary>
    public bool OnFrame(in AacpFrame frame)
    {
        if (!ControlNotification.TryParse(frame, out var notification) ||
            notification.Identifier != AacpControlIdentifiers.ListeningMode ||
            !ListeningModeWire.TryFromWire(notification.Value1, out var mode))
        {
            return false;
        }

        TaskCompletionSource<NoiseControlMode>? pending;
        lock (_sync)
        {
            ReportedMode = mode;
            ReportedAt = _time.GetUtcNow();
            pending = _pending;
            _pending = null;
        }

        pending?.TrySetResult(mode);
        ModeReported?.Invoke(this, mode);
        return true;
    }

    public async Task<ListeningModeResult> RequestAsync(
        NoiseControlMode mode,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var packet = AacpPacketFactory.CreateNoiseControl(mode);
        var completion = new TaskCompletionSource<NoiseControlMode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pending?.TrySetCanceled(CancellationToken.None);
            _pending = completion;
        }

        try
        {
            await _send(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ClearPending(completion);
            return new ListeningModeResult(CommandOutcome.NotSent, mode, ReportedMode, exception.Message);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout ?? DefaultTimeout, _time, timeoutSource.Token);
        var finished = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);
        await timeoutSource.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (finished != completion.Task || !completion.Task.IsCompletedSuccessfully)
        {
            ClearPending(completion);
            return new ListeningModeResult(
                CommandOutcome.Unconfirmed,
                mode,
                ReportedMode,
                "Command sent, but the AirPods did not report the new mode.");
        }

        var reported = completion.Task.Result;
        return reported == mode
            ? new ListeningModeResult(CommandOutcome.Confirmed, mode, reported, "Confirmed by the AirPods.")
            : new ListeningModeResult(
                CommandOutcome.Rejected,
                mode,
                reported,
                $"The AirPods stayed in {reported}; {mode} may be unsupported or disabled.");
    }

    private void ClearPending(TaskCompletionSource<NoiseControlMode> completion)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_pending, completion))
            {
                _pending = null;
            }
        }
    }
}
