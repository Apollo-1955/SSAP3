using SSAP3.Core.Connection;
using SSAP3.Core.Motion;

namespace SSAP3.Core.Tests;

public class ReconnectPolicyTests
{
    [Fact]
    public void Delays_grow_exponentially_and_are_capped()
    {
        var policy = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(500), MaximumDelay = TimeSpan.FromSeconds(30), Jitter = 0 };
        Assert.Equal(500, policy.DelayFor(1, 0.5).TotalMilliseconds, 3);
        Assert.Equal(1000, policy.DelayFor(2, 0.5).TotalMilliseconds, 3);
        Assert.Equal(4000, policy.DelayFor(4, 0.5).TotalMilliseconds, 3);
        Assert.Equal(30000, policy.DelayFor(20, 0.5).TotalMilliseconds, 3);
    }

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var policy = new ReconnectPolicy { Jitter = 0.2 };
        Assert.Equal(400, policy.DelayFor(1, 0).TotalMilliseconds, 3);
        Assert.Equal(600, policy.DelayFor(1, 1).TotalMilliseconds, 3);
    }
}

public class ControlChannelSupervisorTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(100);

    private static ControlChannelSupervisor Create(int maximumAttempts = 4) =>
        new(new ReconnectPolicy { MaximumAttempts = maximumAttempts, Jitter = 0 }, () => 0.5);

    [Fact]
    public void Connecting_the_airpods_opens_exactly_one_session()
    {
        var supervisor = Create();
        Assert.Equal(SupervisorAction.OpenSession, supervisor.OnLinkChanged(DeviceLink.Connected, Start));
        Assert.Equal(ControlChannelState.Connecting, supervisor.State);

        // Duplicate events while an attempt is in flight never open a second session.
        Assert.Equal(SupervisorAction.None, supervisor.OnLinkChanged(DeviceLink.Connected, Start));
        Assert.Equal(SupervisorAction.None, supervisor.OnUserRetry(Start));
        Assert.Equal(SupervisorAction.None, supervisor.Tick(Start + TimeSpan.FromMinutes(1)));

        supervisor.OnSessionOpened();
        Assert.Equal(ControlChannelState.Ready, supervisor.State);
        Assert.Equal(SupervisorAction.None, supervisor.OnLinkChanged(DeviceLink.Connected, Start));
    }

    [Fact]
    public void Failures_back_off_and_stop_after_the_retry_budget()
    {
        var supervisor = Create(maximumAttempts: 3);
        var now = Start;
        Assert.Equal(SupervisorAction.OpenSession, supervisor.OnLinkChanged(DeviceLink.Connected, now));

        var opens = 1;
        for (var iteration = 0; iteration < 50; iteration++)
        {
            supervisor.OnSessionFailed("L2CAP refused", now);
            if (supervisor.State == ControlChannelState.Failed)
            {
                break;
            }

            var due = supervisor.NextRetryAt!.Value;
            Assert.True(due > now);
            Assert.Equal(SupervisorAction.None, supervisor.Tick(due - TimeSpan.FromMilliseconds(1)));
            now = due;
            Assert.Equal(SupervisorAction.OpenSession, supervisor.Tick(now));
            opens++;
        }

        Assert.Equal(ControlChannelState.Failed, supervisor.State);
        Assert.Equal(3, opens);
        Assert.Equal(SupervisorAction.None, supervisor.Tick(now + TimeSpan.FromHours(1)));

        // An explicit user action starts a fresh episode.
        Assert.Equal(SupervisorAction.OpenSession, supervisor.OnUserRetry(now));
    }

    [Fact]
    public void Missing_driver_never_triggers_attempts()
    {
        var supervisor = Create();
        supervisor.OnAvailabilityChanged(ControlUnavailableReason.DriverNotInstalled, Start);
        Assert.Equal(SupervisorAction.None, supervisor.OnLinkChanged(DeviceLink.Connected, Start));
        Assert.Equal(SupervisorAction.None, supervisor.OnUserRetry(Start));
        Assert.Equal(SupervisorAction.None, supervisor.Tick(Start + TimeSpan.FromHours(1)));
        Assert.Equal(ControlChannelState.Unavailable, supervisor.State);

        Assert.Equal(SupervisorAction.OpenSession, supervisor.OnAvailabilityChanged(ControlUnavailableReason.None, Start));
    }

    [Fact]
    public void Lost_session_reconnects_after_a_short_delay()
    {
        var supervisor = Create();
        supervisor.OnLinkChanged(DeviceLink.Connected, Start);
        supervisor.OnSessionOpened();
        Assert.Equal(SupervisorAction.None, supervisor.OnSessionLost("Stale channel", Start));
        Assert.Equal(ControlChannelState.WaitingToRetry, supervisor.State);
        Assert.Equal(SupervisorAction.OpenSession, supervisor.Tick(supervisor.NextRetryAt!.Value));
    }

    [Fact]
    public void Sleep_closes_and_resume_reopens()
    {
        var supervisor = Create();
        supervisor.OnLinkChanged(DeviceLink.Connected, Start);
        supervisor.OnSessionOpened();
        Assert.Equal(SupervisorAction.CloseSession, supervisor.OnSuspend());
        Assert.Equal(SupervisorAction.None, supervisor.Tick(Start + TimeSpan.FromMinutes(5)));
        Assert.Equal(SupervisorAction.OpenSession, supervisor.OnResume(Start + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Disconnecting_closes_the_session_and_resets()
    {
        var supervisor = Create();
        supervisor.OnLinkChanged(DeviceLink.Connected, Start);
        supervisor.OnSessionOpened();
        Assert.Equal(SupervisorAction.CloseSession, supervisor.OnLinkChanged(DeviceLink.PairedDisconnected, Start));
        Assert.Equal(ControlChannelState.Idle, supervisor.State);
        Assert.Equal(SupervisorAction.OpenSession, supervisor.OnLinkChanged(DeviceLink.Connected, Start));
    }
}

public class DeviceStatusTests
{
    [Fact]
    public void Reports_audio_only_when_the_driver_is_missing()
    {
        var supervisor = new ControlChannelSupervisor();
        supervisor.OnAvailabilityChanged(ControlUnavailableReason.DriverNotInstalled, TimeSpan.Zero);
        supervisor.OnLinkChanged(DeviceLink.Connected, TimeSpan.Zero);
        var status = DeviceStatus.From(supervisor, MotionStreamStatus.Inactive);
        Assert.Equal(DeviceStatusKind.ConnectedNoControl, status.Kind);
        Assert.Contains("driver", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MotionStreamStatus.Inactive, DeviceStatusKind.ConnectedNoMotion)]
    [InlineData(MotionStreamStatus.WaitingForFirstFrame, DeviceStatusKind.MotionStarting)]
    [InlineData(MotionStreamStatus.Streaming, DeviceStatusKind.MotionStreaming)]
    [InlineData(MotionStreamStatus.Stalled, DeviceStatusKind.MotionStalled)]
    [InlineData(MotionStreamStatus.Unsupported, DeviceStatusKind.MotionUnsupported)]
    public void Maps_motion_states_when_controls_are_ready(MotionStreamStatus motion, DeviceStatusKind expected)
    {
        var supervisor = new ControlChannelSupervisor();
        supervisor.OnLinkChanged(DeviceLink.Connected, TimeSpan.Zero);
        supervisor.OnSessionOpened();
        Assert.Equal(expected, DeviceStatus.From(supervisor, motion, 50).Kind);
    }

    [Fact]
    public void Bluetooth_off_takes_precedence()
    {
        var supervisor = new ControlChannelSupervisor();
        supervisor.OnLinkChanged(DeviceLink.Connected, TimeSpan.Zero);
        supervisor.OnSessionOpened();
        Assert.Equal(DeviceStatusKind.BluetoothOff, DeviceStatus.From(supervisor, MotionStreamStatus.Streaming, 50, bluetoothRadioOff: true).Kind);
    }

    [Fact]
    public void Reports_paired_but_disconnected()
    {
        var supervisor = new ControlChannelSupervisor();
        supervisor.OnLinkChanged(DeviceLink.PairedDisconnected, TimeSpan.Zero);
        Assert.Equal(DeviceStatusKind.PairedDisconnected, DeviceStatus.From(supervisor, MotionStreamStatus.Inactive).Kind);
    }
}
