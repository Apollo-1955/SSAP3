using SSAP3.Core;
using SSAP3.Core.Aacp;

namespace SSAP3.Core.Tests;

public class ListeningModeControllerTests
{
    private static void Report(ListeningModeController controller, byte wireMode)
    {
        var packet = new byte[] { 0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0D, wireMode, 0x00, 0x00, 0x00 };
        Assert.True(AacpFrame.TryParse(packet, out var frame));
        Assert.True(controller.OnFrame(frame));
    }

    [Fact]
    public async Task Mode_change_is_confirmed_only_by_the_device_echo()
    {
        ListeningModeController? controller = null;
        var sent = new List<byte[]>();
        controller = new ListeningModeController((packet, _) =>
        {
            sent.Add(packet);
            Report(controller!, 0x03);
            return Task.CompletedTask;
        });

        var result = await controller.RequestAsync(NoiseControlMode.Transparency, TimeSpan.FromSeconds(5));

        Assert.Equal(CommandOutcome.Confirmed, result.Outcome);
        Assert.Equal(NoiseControlMode.Transparency, controller.ReportedMode);
        Assert.Equal("0400040009000D03000000", Convert.ToHexString(Assert.Single(sent)));
    }

    [Fact]
    public async Task Different_echo_is_reported_as_rejected()
    {
        ListeningModeController? controller = null;
        controller = new ListeningModeController((_, _) =>
        {
            Report(controller!, 0x02);
            return Task.CompletedTask;
        });

        var result = await controller.RequestAsync(NoiseControlMode.Adaptive, TimeSpan.FromSeconds(5));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(NoiseControlMode.NoiseCancellation, result.Reported);
    }

    [Fact]
    public async Task Missing_echo_is_reported_as_unconfirmed()
    {
        var controller = new ListeningModeController((_, _) => Task.CompletedTask);

        var result = await controller.RequestAsync(NoiseControlMode.NoiseCancellation, TimeSpan.FromMilliseconds(50));

        Assert.Equal(CommandOutcome.Unconfirmed, result.Outcome);
        Assert.Null(controller.ReportedMode);
    }

    [Fact]
    public async Task Transport_failure_is_reported_as_not_sent()
    {
        var controller = new ListeningModeController((_, _) => throw new IOException("Driver unavailable"));

        var result = await controller.RequestAsync(NoiseControlMode.Off, TimeSpan.FromSeconds(1));

        Assert.Equal(CommandOutcome.NotSent, result.Outcome);
        Assert.Contains("Driver unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsolicited_reports_update_state_and_do_not_confuse_later_requests()
    {
        var controller = new ListeningModeController((_, _) => Task.CompletedTask);
        Report(controller, 0x04);
        Assert.Equal(NoiseControlMode.Adaptive, controller.ReportedMode);

        var pending = controller.RequestAsync(NoiseControlMode.Off, TimeSpan.FromSeconds(5));
        Report(controller, 0x01);
        var result = await pending;

        Assert.Equal(CommandOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public void Ignores_other_control_identifiers()
    {
        var controller = new ListeningModeController((_, _) => Task.CompletedTask);
        Assert.True(AacpFrame.TryParse(Convert.FromHexString("0400040009002801000000"), out var frame));
        Assert.False(controller.OnFrame(frame));
        Assert.Null(controller.ReportedMode);
    }
}
