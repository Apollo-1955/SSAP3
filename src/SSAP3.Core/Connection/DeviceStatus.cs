using SSAP3.Core.Motion;

namespace SSAP3.Core.Connection;

public enum StatusSeverity
{
    Good,
    Info,
    Warning,
    Error,
}

/// <summary>The user-facing connection and motion status. Every case maps to a real, observed condition.</summary>
public enum DeviceStatusKind
{
    NotPaired,
    PairedDisconnected,
    ConnectedNoControl,
    Connecting,
    Reconnecting,
    ConnectedNoMotion,
    MotionStarting,
    MotionStreaming,
    MotionStalled,
    MotionUnsupported,
    ConnectionFailed,
    Suspended,
    BluetoothOff,
}

public sealed record DeviceStatus(DeviceStatusKind Kind, string Title, string Detail, StatusSeverity Severity)
{
    public static DeviceStatus From(
        ControlChannelSupervisor supervisor,
        MotionStreamStatus motion,
        double motionRateHz = 0,
        bool bluetoothRadioOff = false)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        if (bluetoothRadioOff)
        {
            return new(DeviceStatusKind.BluetoothOff, "Bluetooth is off", "Turn on Bluetooth in Windows Settings to use your AirPods.", StatusSeverity.Warning);
        }

        if (supervisor.Link == DeviceLink.NotPaired)
        {
            return new(DeviceStatusKind.NotPaired, "Not paired", "Pair AirPods Pro 3 in Windows Bluetooth settings.", StatusSeverity.Info);
        }

        if (supervisor.Link == DeviceLink.PairedDisconnected)
        {
            return new(DeviceStatusKind.PairedDisconnected, "Paired, not connected", "Open the case or put the AirPods in your ears.", StatusSeverity.Info);
        }

        return supervisor.State switch
        {
            ControlChannelState.Unavailable => new(
                DeviceStatusKind.ConnectedNoControl,
                "Connected (audio only)",
                UnavailableText(supervisor.UnavailableReason),
                StatusSeverity.Warning),
            ControlChannelState.Connecting when supervisor.Attempt <= 1 => new(
                DeviceStatusKind.Connecting, "Connecting controls…", "Opening the AirPods control channel.", StatusSeverity.Info),
            ControlChannelState.Connecting or ControlChannelState.WaitingToRetry => new(
                DeviceStatusKind.Reconnecting,
                "Reconnecting…",
                $"Attempt {supervisor.Attempt}. {supervisor.LastError ?? string.Empty}".Trim(),
                StatusSeverity.Warning),
            ControlChannelState.Failed => new(
                DeviceStatusKind.ConnectionFailed,
                "Controls unavailable",
                "Several attempts failed. Reconnect the AirPods or select Reconnect.",
                StatusSeverity.Error),
            ControlChannelState.Suspended => new(
                DeviceStatusKind.Suspended, "Paused for sleep", "The connection resumes after Windows wakes.", StatusSeverity.Info),
            ControlChannelState.Ready => MotionStatus(motion, motionRateHz),
            _ => new(DeviceStatusKind.ConnectedNoControl, "Connected", "Control channel idle.", StatusSeverity.Info),
        };
    }

    private static DeviceStatus MotionStatus(MotionStreamStatus motion, double rateHz) => motion switch
    {
        MotionStreamStatus.WaitingForFirstFrame => new(
            DeviceStatusKind.MotionStarting, "Starting head tracking…", "Waiting for the first motion packet.", StatusSeverity.Info),
        MotionStreamStatus.Streaming => new(
            DeviceStatusKind.MotionStreaming, "Head tracking active", $"Receiving motion at {rateHz:0} Hz.", StatusSeverity.Good),
        MotionStreamStatus.Stalled => new(
            DeviceStatusKind.MotionStalled,
            "Motion stalled",
            "Motion packets stopped. Holding the soundstage, then easing back to centre.",
            StatusSeverity.Warning),
        MotionStreamStatus.Unsupported => new(
            DeviceStatusKind.MotionUnsupported,
            "Motion not available",
            "The AirPods did not stream motion data. Fixed soundstage only.",
            StatusSeverity.Warning),
        _ => new(DeviceStatusKind.ConnectedNoMotion, "Connected", "Controls ready. Head tracking is off.", StatusSeverity.Good),
    };

    private static string UnavailableText(ControlUnavailableReason reason) => reason switch
    {
        ControlUnavailableReason.DriverNotInstalled =>
            "Listening modes and head tracking need the SSAP3 driver, which is not installed. It is only shipped once Microsoft has signed it.",
        ControlUnavailableReason.ServiceNotExposed =>
            "Windows did not expose the AirPods control service. Reconnect the AirPods.",
        ControlUnavailableReason.IncompatibleDriver =>
            "The installed SSAP3 driver does not match this app version. Reinstall SSAP3.",
        _ => "Control channel unavailable.",
    };
}
