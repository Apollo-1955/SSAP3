namespace SoundStage.Core;

public enum ConnectionState
{
    Disconnected,
    Discovering,
    ConnectingAudio,
    ConnectingControl,
    Ready,
    Degraded,
    Reconnecting,
    Failed,
}

public enum ConnectionEvent
{
    Start,
    DeviceSeen,
    AudioConnected,
    ControlConnected,
    ConnectionLost,
    Timeout,
    Retry,
    Stop,
}

public sealed record ConnectionSnapshot(
    ConnectionState State,
    bool AudioConnected,
    bool ControlConnected,
    int RetryAttempt,
    DateTimeOffset ChangedAt);

public static class ConnectionStateMachine
{
    public static ConnectionSnapshot Initial(DateTimeOffset now) =>
        new(ConnectionState.Disconnected, false, false, 0, now);

    public static ConnectionSnapshot Apply(
        ConnectionSnapshot current,
        ConnectionEvent connectionEvent,
        DateTimeOffset now) =>
        connectionEvent switch
        {
            ConnectionEvent.Start =>
                current with
                {
                    State = ConnectionState.Discovering,
                    RetryAttempt = 0,
                    ChangedAt = now,
                },
            ConnectionEvent.DeviceSeen =>
                current with
                {
                    State = ConnectionState.ConnectingAudio,
                    ChangedAt = now,
                },
            ConnectionEvent.AudioConnected =>
                current with
                {
                    State = current.ControlConnected
                        ? ConnectionState.Ready
                        : ConnectionState.ConnectingControl,
                    AudioConnected = true,
                    ChangedAt = now,
                },
            ConnectionEvent.ControlConnected =>
                current with
                {
                    State = current.AudioConnected
                        ? ConnectionState.Ready
                        : ConnectionState.Degraded,
                    ControlConnected = true,
                    ChangedAt = now,
                },
            ConnectionEvent.ConnectionLost =>
                current with
                {
                    State = ConnectionState.Reconnecting,
                    AudioConnected = false,
                    ControlConnected = false,
                    RetryAttempt = current.RetryAttempt + 1,
                    ChangedAt = now,
                },
            ConnectionEvent.Timeout when current.RetryAttempt < 5 =>
                current with
                {
                    State = ConnectionState.Reconnecting,
                    RetryAttempt = current.RetryAttempt + 1,
                    ChangedAt = now,
                },
            ConnectionEvent.Timeout =>
                current with
                {
                    State = ConnectionState.Failed,
                    ChangedAt = now,
                },
            ConnectionEvent.Retry =>
                current with
                {
                    State = ConnectionState.Discovering,
                    ChangedAt = now,
                },
            ConnectionEvent.Stop =>
                Initial(now),
            _ => current,
        };

    public static TimeSpan RetryDelay(int attempt)
    {
        var clamped = Math.Clamp(attempt, 0, 5);
        return TimeSpan.FromMilliseconds(300 * Math.Pow(2, clamped));
    }
}
