using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SSAP3.Platform.Windows;

/// <summary>
/// System-wide monotonic time in the same domain as the driver's packet
/// timestamps (KeQueryInterruptTime, which user mode reads with
/// QueryInterruptTime). Falls back to Stopwatch time when the API is
/// unavailable; kernel timestamps are then ignored.
/// </summary>
public static class MonotonicClock
{
    private static readonly bool InterruptTimeAvailable = ProbeInterruptTime();
    private static readonly long StopwatchOrigin = Stopwatch.GetTimestamp();

    public static bool UsesInterruptTime => InterruptTimeAvailable;

    public static TimeSpan Now =>
        InterruptTimeAvailable && TryQueryInterruptTime(out var ticks)
            ? TimeSpan.FromTicks((long)ticks)
            : Stopwatch.GetElapsedTime(StopwatchOrigin);

    /// <summary>Converts a driver receive time into this clock's domain, or null if unusable.</summary>
    public static TimeSpan? FromDriverTime(ulong interruptTime100ns) =>
        InterruptTimeAvailable && interruptTime100ns is > 0 and < long.MaxValue
            ? TimeSpan.FromTicks((long)interruptTime100ns)
            : null;

    private static bool ProbeInterruptTime()
    {
        try
        {
            return TryQueryInterruptTime(out _);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryQueryInterruptTime(out ulong ticks)
    {
        QueryInterruptTime(out ticks);
        return ticks > 0;
    }

    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")]
    private static extern void QueryInterruptTime(out ulong interruptTime);
}

/// <summary>
/// Precise periodic waits without raising the global timer resolution: a
/// high-resolution waitable timer (Windows 10 1803+), with a
/// <see cref="Thread.Sleep(int)"/> fallback.
/// </summary>
public sealed class HighResolutionWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0;
    private readonly nint _timer;

    public HighResolutionWaiter()
    {
        _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    public bool IsHighResolution => _timer != 0;

    public void Wait(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        if (_timer == 0)
        {
            Thread.Sleep(interval);
            return;
        }

        var dueTime = -interval.Ticks;
        if (!SetWaitableTimerEx(_timer, ref dueTime, 0, 0, 0, 0, 0) ||
            WaitForSingleObject(_timer, (uint)Math.Min(int.MaxValue, interval.TotalMilliseconds + 100)) != WaitObject0)
        {
            Thread.Sleep(interval);
        }
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            _ = CloseHandle(_timer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(nint timer, ref long dueTime, int period, nint completionRoutine, nint argument, nint wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
