namespace SoundStage.Core;

public sealed record AirPodsBatteryUpdate(
    int? LeftLevel,
    bool IsLeftCharging,
    int? RightLevel,
    bool IsRightCharging,
    int? CaseLevel,
    bool IsCaseCharging);

public sealed class AirPodsBatteryPacketParser
{
    private static readonly byte[] Prefix = [0x04, 0x00, 0x04, 0x00, 0x04, 0x00];

    public bool TryParse(
        ReadOnlySpan<byte> packet,
        out AirPodsBatteryUpdate? update)
    {
        update = null;
        if (packet.Length < 7 ||
            !packet[..Prefix.Length].SequenceEqual(Prefix))
        {
            return false;
        }

        var componentCount = packet[6];
        if (componentCount is 0 or > 3 ||
            packet.Length != 7 + componentCount * 5)
        {
            return false;
        }

        int? leftLevel = null;
        int? rightLevel = null;
        int? caseLevel = null;
        var leftCharging = false;
        var rightCharging = false;
        var caseCharging = false;

        for (var index = 0; index < componentCount; index++)
        {
            var offset = 7 + index * 5;
            if (packet[offset + 1] != 0x01 ||
                packet[offset + 4] != 0x01)
            {
                return false;
            }

            var component = packet[offset];
            var level = Math.Clamp(packet[offset + 2], (byte)0, (byte)100);
            var state = packet[offset + 3];
            if (state == 0x04)
            {
                continue;
            }

            var charging = state == 0x01;
            switch (component)
            {
                case 0x04:
                    leftLevel = level;
                    leftCharging = charging;
                    break;
                case 0x02:
                    rightLevel = level;
                    rightCharging = charging;
                    break;
                case 0x08:
                    caseLevel = level;
                    caseCharging = charging;
                    break;
            }
        }

        update = new AirPodsBatteryUpdate(
            leftLevel,
            leftCharging,
            rightLevel,
            rightCharging,
            caseLevel,
            caseCharging);
        return true;
    }
}
