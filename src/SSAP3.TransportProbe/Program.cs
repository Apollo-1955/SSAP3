using SSAP3.Core;
using SSAP3.Core.Aacp;
using SSAP3.Platform.Windows;

// Developer tool for hardware validation (HARDWARE_COMPATIBILITY.md). Prints raw
// AACP traffic. Do not share its output publicly: it can include identifiers.

if (args.Length >= 1 && args[0].Equals("--ble", StringComparison.OrdinalIgnoreCase))
{
    // Driverless check: listen to Apple proximity advertisements (no pairing keys,
    // so identities are NOT verified). Addresses are redacted in the output.
    var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? Math.Clamp(parsed, 1, 120) : 20;
    using var scanner = new WindowsBleAirPodsScanner();
    var seen = 0;
    scanner.AdvertisementReceived += (_, e) =>
    {
        Interlocked.Increment(ref seen);
        var s = e.Status;
        Console.WriteLine(
            $"{DateTime.Now:T} {DiagnosticRedactor.Redact(s.Address)} rssi={e.Rssi} model=0x{s.ModelId:X4} ({s.Model}) " +
            $"L={s.LeftBattery?.ToString() ?? "-"} R={s.RightBattery?.ToString() ?? "-"} case={s.CaseBattery?.ToString() ?? "-"} " +
            $"inEar={s.IsLeftInEar}/{s.IsRightInEar} lid={(s.LidOpen ? "open" : "closed")} state={s.ConnectionState}");
    };
    scanner.StatusChanged += (_, message) => Console.WriteLine($"[scanner] {message}");
    scanner.Start();
    Console.WriteLine($"Listening for Apple proximity advertisements for {seconds} s...");
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"Advertisements parsed: {seen}");
    return 0;
}

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine(
        "Usage: SSAP3-TransportProbe <Bluetooth address> [Off|NoiseCancellation|Transparency|Adaptive|Motion]");
    return 64;
}

if (args.Length == 2 &&
    !args[1].Equals("Motion", StringComparison.OrdinalIgnoreCase) &&
    !Enum.TryParse<NoiseControlMode>(args[1], ignoreCase: true, out _))
{
    Console.Error.WriteLine($"Unknown listening mode: {args[1]}");
    return 64;
}

Console.WriteLine("Opening the SSAP3 AAP driver...");
using var result = Ssap3DriverAacpClient.TryConnect(args[0]);
Console.WriteLine($"{result.Outcome}: {result.Message}");
if (!result.Connected)
{
    return 2;
}

var client = result.Client!;
Console.WriteLine($"MaximumPacketSize={client.MaximumPacketSize}; kernel timestamps={MonotonicClock.UsesInterruptTime}");
if (args.Length == 1)
{
    return 0;
}

client.Send(AacpPacketFactory.CreateHandshake());
client.Send(AacpPacketFactory.CreateFeatureFlags());
client.Send(AacpPacketFactory.CreateNotificationRequest());
var motion = args[1].Equals("Motion", StringComparison.OrdinalIgnoreCase);
if (motion)
{
    client.Send(AacpPacketFactory.CreateOwnsConnection(true));
    Thread.Sleep(500);
    client.Send(AacpPacketFactory.CreateAlternateStartHeadTracking());
    Console.WriteLine("Motion stream requested. Move your head for ten seconds.");
}
else
{
    var mode = Enum.Parse<NoiseControlMode>(args[1], ignoreCase: true);
    client.Send(AacpPacketFactory.CreateNoiseControl(mode));
    Console.WriteLine($"Requested {mode} ({Convert.ToHexString(AacpPacketFactory.CreateNoiseControl(mode))}); waiting for the echo...");
}

var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(motion ? 10 : 3);
var frames = 0;
while (DateTime.UtcNow < deadline)
{
    var packet = client.Receive();
    if (packet.IsEmpty)
    {
        Thread.Sleep(5);
        continue;
    }

    var now = MonotonicClock.Now;
    var age = MonotonicClock.FromDriverTime(packet.ReceiveInterruptTime) is { } received ? (now - received).TotalMilliseconds : double.NaN;
    if (AacpFrame.TryParse(packet.Data, out var frame) && frame.Opcode == AacpOpcodes.HeadTracking)
    {
        frames++;
        if (frames % 25 != 1)
        {
            continue;
        }
    }

    Console.WriteLine($"[{age,6:0.0} ms] {Convert.ToHexString(packet.Data)}");
}

if (motion)
{
    client.Send(AacpPacketFactory.CreateAlternateStopHeadTracking());
    Console.WriteLine($"Head-tracking frames received: {frames} ({frames / 10.0:0.0} per second)");
}

return 0;
