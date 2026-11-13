using SSAP3.Core;

namespace SSAP3.Core.Tests;

public class RedactorTests
{
    [Fact]
    public void Redacts_separated_bluetooth_addresses_keeping_two_octets() =>
        Assert.Equal(
            "AirPods at XX:XX:XX:XX:E5:F6 connected",
            DiagnosticRedactor.Redact("AirPods at A1:B2:C3:D4:E5:F6 connected"));

    [Fact]
    public void Redacts_addresses_inside_pnp_instance_ids()
    {
        var redacted = DiagnosticRedactor.Redact(
            @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&2027\7&1a2b3c4d&0&A1B2C3D4E5F6_C00000000");
        Assert.DoesNotContain("A1B2C3D4", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XXXXXXXXE5F6", redacted, StringComparison.Ordinal);
        Assert.Contains("PID&2027", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_user_profile_paths_and_device_names()
    {
        var redacted = DiagnosticRedactor.Redact(
            @"Log at C:\Users\someone\AppData\Local\SSAP3\SSAP3.log for Alex's AirPods Pro 3",
            ["Alex's AirPods Pro 3"]);
        Assert.Equal(@"Log at C:\Users\<user>\AppData\Local\SSAP3\SSAP3.log for <device name>", redacted);
    }

    [Fact]
    public void Leaves_ordinary_text_alone() =>
        Assert.Equal("Build 26200, PID&2027", DiagnosticRedactor.Redact("Build 26200, PID&2027"));
}
