using SoundStage.Core;
using SoundStage.Platform.Windows;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine(
        "Usage: SoundStage-TransportProbe <Bluetooth address> [Off|NoiseCancellation|Transparency|Adaptive|Motion]");
    return 64;
}

if (args.Length == 2 &&
    !args[1].Equals("Motion", StringComparison.OrdinalIgnoreCase) &&
    !Enum.TryParse<NoiseControlMode>(args[1], ignoreCase: true, out _))
{
    Console.Error.WriteLine($"Unknown noise-control mode: {args[1]}");
    return 64;
}

Console.WriteLine($"Opening the SoundStage AACP driver for {args[0]}...");
using var result = SoundStageDriverAacpClient.TryConnect(args[0]);
Console.WriteLine(result.Message);
if (!result.Connected || result.Client is null)
{
    return 2;
}

Console.WriteLine($"DevicePath={result.Client.DevicePath}");
Console.WriteLine($"MaximumPacketSize={result.Client.MaximumPacketSize}");

if (args.Length == 1)
{
    return 0;
}

result.Client.Send(AacpPacketFactory.CreateHandshake());
result.Client.Send(AacpPacketFactory.CreateFeatureFlags());
result.Client.Send(AacpPacketFactory.CreateNotificationRequest());
var readLimit = 64;
if (args[1].Equals("Motion", StringComparison.OrdinalIgnoreCase))
{
    result.Client.Send(AacpPacketFactory.CreateOwnsConnection(true));
    Thread.Sleep(500);
    result.Client.Send(AacpPacketFactory.CreateAlternateStartHeadTracking());
    Console.WriteLine("Motion stream requested. Move your head for a few seconds.");
    readLimit = 256;
}
else
{
    var mode = Enum.Parse<NoiseControlMode>(args[1], ignoreCase: true);
    result.Client.Send(AacpPacketFactory.CreateNoiseControl(mode));
    Console.WriteLine($"Mode={mode}; command sent.");
}

Thread.Sleep(100);

try
{
    var receivedPackets = 0;
    for (var index = 0; index < readLimit; index++)
    {
        var response = result.Client.Receive();
        if (response.Length == 0)
        {
            if (args[1].Equals("Motion", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(20);
                continue;
            }

            break;
        }

        receivedPackets++;
        Console.WriteLine($"Response[{index}]={Convert.ToHexString(response)}");
    }

    if (args[1].Equals("Motion", StringComparison.OrdinalIgnoreCase))
    {
        result.Client.Send(AacpPacketFactory.CreateAlternateStopHeadTracking());
        Console.WriteLine($"ReceivedPackets={receivedPackets}");
    }
}
catch (Exception exception)
{
    Console.WriteLine($"ReadWarning={exception.Message}");
}

return 0;
