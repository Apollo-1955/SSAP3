namespace SSAP3.Core.Motion;

public enum MotionStreamStatus
{
    /// <summary>Motion was not requested.</summary>
    Inactive,

    /// <summary>Start packets were sent; no frame has arrived yet.</summary>
    WaitingForFirstFrame,

    /// <summary>Frames are arriving.</summary>
    Streaming,

    /// <summary>Frames were arriving but stopped.</summary>
    Stalled,

    /// <summary>No frame ever arrived within the start timeout: the device or firmware did not stream.</summary>
    Unsupported,
}

/// <summary>Tracks the real motion-stream status shown to the user. Never synthesises frames.</summary>
public sealed class MotionStreamMonitor
{
    private TimeSpan _startedAt;
    private TimeSpan _lastFrameAt;
    private double _rateHz;

    public MotionStreamMonitor(TimeSpan? stallAfter = null, TimeSpan? unsupportedAfter = null)
    {
        StallAfter = stallAfter ?? TimeSpan.FromSeconds(1);
        UnsupportedAfter = unsupportedAfter ?? TimeSpan.FromSeconds(6);
    }

    public TimeSpan StallAfter { get; }

    public TimeSpan UnsupportedAfter { get; }

    public bool Active { get; private set; }

    public long FramesReceived { get; private set; }

    public long StallCount { get; private set; }

    public double FrameRateHz => _rateHz;

    private MotionStreamStatus _lastStatus = MotionStreamStatus.Inactive;

    public void Start(TimeSpan now)
    {
        Active = true;
        _startedAt = now;
        _lastFrameAt = TimeSpan.Zero;
        FramesReceived = 0;
        _rateHz = 0;
        _lastStatus = MotionStreamStatus.WaitingForFirstFrame;
    }

    public void Stop()
    {
        Active = false;
        _lastStatus = MotionStreamStatus.Inactive;
    }

    public void OnFrame(TimeSpan now)
    {
        if (!Active)
        {
            return;
        }

        if (FramesReceived > 0 && now > _lastFrameAt)
        {
            var interval = (now - _lastFrameAt).TotalSeconds;
            if (interval < StallAfter.TotalSeconds)
            {
                var instant = 1.0 / interval;
                _rateHz = _rateHz <= 0 ? instant : _rateHz + (0.05 * (instant - _rateHz));
            }
        }

        _lastFrameAt = now;
        FramesReceived++;
    }

    public MotionStreamStatus Evaluate(TimeSpan now)
    {
        MotionStreamStatus status;
        if (!Active)
        {
            status = MotionStreamStatus.Inactive;
        }
        else if (FramesReceived == 0)
        {
            status = now - _startedAt >= UnsupportedAfter
                ? MotionStreamStatus.Unsupported
                : MotionStreamStatus.WaitingForFirstFrame;
        }
        else
        {
            status = now - _lastFrameAt >= StallAfter ? MotionStreamStatus.Stalled : MotionStreamStatus.Streaming;
        }

        if (status == MotionStreamStatus.Stalled && _lastStatus != MotionStreamStatus.Stalled)
        {
            StallCount++;
        }

        _lastStatus = status;
        return status;
    }
}
