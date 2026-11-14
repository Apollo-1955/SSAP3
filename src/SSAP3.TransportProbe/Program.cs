using SSAP3.Core;
using SSAP3.Core.Aacp;
using SSAP3.Core.Motion;
using SSAP3.Platform.Windows;

// Developer tool for hardware validation (HARDWARE_COMPATIBILITY.md). Prints raw
// AACP traffic. Do not share its output publicly: it can include identifiers.

if (args.Length >= 1 && args[0].Equals("--investigate", StringComparison.OrdinalIgnoreCase))
{
    // Not awaited: an async Main could not hold the ref-struct parser results used below.
    return SSAP3.TransportProbe.MotionAccessInvestigation.RunAsync(args.Length > 1 ? args[1] : null).GetAwaiter().GetResult();
}

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
var controller = new MotionStreamController();
var decoder = new DeviceMotionOrientationDecoder();
const int MotionSeconds = 20;
if (motion)
{
    client.Send(AacpPacketFactory.CreateOwnsConnection(true));
    Thread.Sleep(500);
    foreach (var command in controller.Start(MonotonicClock.Now))
    {
        client.Send(command);
        Console.WriteLine($"> start {SensorDataWx.Describe(command)}");
    }

    Console.WriteLine($"Motion stream requested. Face the screen, then turn your head left and right for {MotionSeconds} seconds.");
}
else
{
    var mode = Enum.Parse<NoiseControlMode>(args[1], ignoreCase: true);
    client.Send(AacpPacketFactory.CreateNoiseControl(mode));
    Console.WriteLine($"Requested {mode} ({Convert.ToHexString(AacpPacketFactory.CreateNoiseControl(mode))}); waiting for the echo...");
}

var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(motion ? MotionSeconds : 3);
var frames = 0;
var largestPacket = 0;
while (DateTime.UtcNow < deadline)
{
    var now = MonotonicClock.Now;
    if (motion)
    {
        var status = frames == 0 ? MotionStreamStatus.WaitingForFirstFrame : MotionStreamStatus.Streaming;
        foreach (var command in controller.Tick(now, status))
        {
            client.Send(command);
            Console.WriteLine($"> start {SensorDataWx.Describe(command)}");
        }
    }

    var packet = client.Receive();
    if (packet.IsEmpty)
    {
        Thread.Sleep(5);
        continue;
    }

    largestPacket = Math.Max(largestPacket, packet.Data.Length);
    var age = MonotonicClock.FromDriverTime(packet.ReceiveInterruptTime) is { } received ? (now - received).TotalMilliseconds : double.NaN;
    if (SensorDataWx.TryParse(packet.Data, out var message))
    {
        if (message.HasAnnouncement)
        {
            controller.OnAnnouncement(message.AnnouncedService, message.AnnouncementPayload);
            Console.WriteLine($"[{age,6:0.0} ms] {SensorDataWx.Describe(packet.Data)}");
        }

        if (HeadTrackingFrameParser.TryParse(packet.Data, out var motionFrame) && controller.AcceptReport(motionFrame.Service))
        {
            frames++;
            if (decoder.TryDecode(motionFrame, out var orientation) && frames % 10 == 1)
            {
                var (yaw, _, _) = QuaternionMath.ToYawPitchRollDegrees(orientation);
                var report = motionFrame.Report;
                var gravity = MathF.Sqrt((float)report.GravityX * report.GravityX + (float)report.GravityY * report.GravityY + (float)report.GravityZ * report.GravityZ) / DeviceMotionReport.CountsPerG;
                Console.WriteLine(
                    $"[{age,6:0.0} ms] service {motionFrame.Service} len={motionFrame.PacketLength} yaw={yaw,7:0.0}° (+ = left) " +
                    $"rate={report.RotationRateDegreesPerSecond,6:0}°/s |g|={gravity:0.00} q15=({report.QuaternionX},{report.QuaternionY},{report.QuaternionZ}) reanchors={decoder.Reanchors}");
            }
        }
        else if (!message.HasAnnouncement)
        {
            Console.WriteLine($"[{age,6:0.0} ms] {SensorDataWx.Describe(packet.Data)} {Convert.ToHexString(packet.Data.AsSpan(0, Math.Min(packet.Data.Length, 96)))}");
        }

        continue;
    }

    Console.WriteLine($"[{age,6:0.0} ms] {Convert.ToHexString(packet.Data.AsSpan(0, Math.Min(packet.Data.Length, 96)))}{(packet.Data.Length > 96 ? "..." : string.Empty)}");
}

if (motion)
{
    foreach (var command in controller.Stop())
    {
        client.Send(command);
    }

    Console.WriteLine(
        $"Motion reports used: {frames} ({frames / (double)MotionSeconds:0.0} per second) from service {controller.ActiveService?.ToString() ?? "none"}; " +
        $"devmotion6 announced as service {controller.DiscoveredService?.ToString() ?? "none"}; largest packet {largestPacket} B.");
}

return 0;
