using System.Security.Cryptography;

namespace SSAP3.Core;

// Adapted from the GPL-3.0 LibrePods Windows port.
public sealed class AirPodsAdvertisementParser
{
    private readonly Dictionary<string, int> _lastValidCaseBatteryByAddress =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<int, string> ModelNames =
        new Dictionary<int, string>
        {
            [0x0E20] = "AirPods Pro",
            [0x1420] = "AirPods Pro 2",
            [0x2420] = "AirPods Pro 2 (USB-C)",
            [0x0220] = "AirPods 1",
            [0x0F20] = "AirPods 2",
            [0x1320] = "AirPods 3",
            [0x1920] = "AirPods 4",
            [0x1B20] = "AirPods 4 (ANC)",
            [0x0A20] = "AirPods Max",
            [0x1F20] = "AirPods Max (USB-C)",
            // Inferred from the Bluetooth DI product ID 0x2027 observed on a
            // paired AirPods Pro 3 (byte-swapped like the entries above).
            // Not yet confirmed from a captured advertisement; see
            // HARDWARE_COMPATIBILITY.md.
            [0x2720] = "AirPods Pro 3",
        };

    /// <summary>Advertisement model ID (byte-swapped Bluetooth product ID) for AirPods Pro 3. Inferred.</summary>
    public const int AirPodsPro3ModelId = 0x2720;

    /// <summary>Converts a Bluetooth Device ID product ID (e.g. 0x2027) to the advertisement model ID (0x2720).</summary>
    public static int ModelIdFromProductId(int productId) =>
        ((productId & 0xFF) << 8) | ((productId >> 8) & 0xFF);

    private static readonly IReadOnlyDictionary<int, string> ColorNames =
        new Dictionary<int, string>
        {
            [0x00] = "White",
            [0x01] = "Black",
            [0x02] = "Red",
            [0x03] = "Blue",
            [0x04] = "Pink",
            [0x05] = "Gray",
            [0x06] = "Silver",
            [0x07] = "Gold",
            [0x08] = "Rose Gold",
            [0x09] = "Space Gray",
            [0x0A] = "Dark Blue",
            [0x0B] = "Light Blue",
            [0x0C] = "Yellow",
        };

    private static readonly IReadOnlyDictionary<int, string> ConnectionStates =
        new Dictionary<int, string>
        {
            [0x00] = "Disconnected",
            [0x04] = "Idle",
            [0x05] = "Music",
            [0x06] = "Call",
            [0x07] = "Ringing",
            [0x09] = "Hanging Up",
            [0xFF] = "Unknown",
        };

    public bool TryParse(
        string address,
        ReadOnlySpan<byte> manufacturerData,
        byte[]? identityResolvingKey,
        byte[]? encryptionKey,
        out AirPodsAdvertisementStatus? status)
    {
        status = null;
        if (manufacturerData.Length < 11 || manufacturerData[0] != 0x07)
        {
            return false;
        }

        var identityResolved = false;
        if (identityResolvingKey is { Length: 16 })
        {
            if (!BluetoothCryptography.VerifyResolvablePrivateAddress(
                    address,
                    identityResolvingKey))
            {
                return false;
            }

            identityResolved = true;
        }

        if (encryptionKey is { Length: 16 } && manufacturerData.Length >= 16)
        {
            var decrypted = TryDecryptLastBlock(manufacturerData, encryptionKey);
            if (decrypted is not null)
            {
                status = ParseEncrypted(address, manufacturerData, decrypted, identityResolved);
                return true;
            }
        }

        status = ParseUnencrypted(address, manufacturerData, identityResolved);
        return true;
    }

    private AirPodsAdvertisementStatus ParseEncrypted(
        string address,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> decrypted,
        bool identityResolved)
    {
        var paired = data[2] == 0x01;
        var modelId = (data[3] << 8) | data[4];
        var statusByte = data[5];
        var primaryLeft = (statusByte & 0x20) != 0;
        var thisPodInCase = (statusByte & 0x40) != 0;
        var xorFactor = primaryLeft ^ thisPodInCase;
        var leftInEar = xorFactor
            ? (statusByte & 0x08) != 0
            : (statusByte & 0x02) != 0;
        var rightInEar = xorFactor
            ? (statusByte & 0x02) != 0
            : (statusByte & 0x08) != 0;
        var flipped = !primaryLeft;
        var leftIndex = flipped ? 2 : 1;
        var rightIndex = flipped ? 1 : 2;

        var (leftCharging, leftBattery) = DecodeEncryptedBattery(decrypted[leftIndex]);
        var (rightCharging, rightBattery) = DecodeEncryptedBattery(decrypted[rightIndex]);
        var (caseCharging, rawCaseBattery) = DecodeEncryptedBattery(decrypted[3]);
        int? caseBattery;
        if (decrypted[3] == 0xFF || (caseCharging && rawCaseBattery == 127))
        {
            caseBattery = _lastValidCaseBatteryByAddress.GetValueOrDefault(address);
        }
        else
        {
            _lastValidCaseBatteryByAddress[address] = rawCaseBattery;
            caseBattery = rawCaseBattery;
        }

        return new AirPodsAdvertisementStatus(
            address,
            DateTimeOffset.UtcNow,
            identityResolved,
            paired,
            ModelNames.GetValueOrDefault(modelId, $"Unknown ({modelId:X4})"),
            leftBattery,
            rightBattery,
            caseBattery,
            leftInEar,
            rightInEar,
            leftCharging,
            rightCharging,
            caseCharging,
            ((data[8] >> 3) & 0x01) == 0,
            ColorNames.GetValueOrDefault(data[9], "Unknown"),
            ConnectionStates.GetValueOrDefault(data[10], $"Unknown ({data[10]})"),
            modelId);
    }

    private static AirPodsAdvertisementStatus ParseUnencrypted(
        string address,
        ReadOnlySpan<byte> data,
        bool identityResolved)
    {
        var paired = data[2] == 0x01;
        var modelId = (data[3] << 8) | data[4];
        var statusByte = data[5];
        var primaryLeft = (statusByte & 0x20) != 0;
        var thisPodInCase = (statusByte & 0x40) != 0;
        var xorFactor = primaryLeft ^ thisPodInCase;
        var leftInEar = xorFactor
            ? (statusByte & 0x08) != 0
            : (statusByte & 0x02) != 0;
        var rightInEar = xorFactor
            ? (statusByte & 0x02) != 0
            : (statusByte & 0x08) != 0;
        var flipped = !primaryLeft;
        var podsBattery = data[6];
        var flagsAndCaseBattery = data[7];
        var leftNibble = flipped ? (podsBattery >> 4) & 0x0F : podsBattery & 0x0F;
        var rightNibble = flipped ? podsBattery & 0x0F : (podsBattery >> 4) & 0x0F;
        var caseNibble = flagsAndCaseBattery & 0x0F;
        var flags = (flagsAndCaseBattery >> 4) & 0x0F;

        return new AirPodsAdvertisementStatus(
            address,
            DateTimeOffset.UtcNow,
            identityResolved,
            paired,
            ModelNames.GetValueOrDefault(modelId, $"Unknown ({modelId:X4})"),
            DecodeBatteryNibble(leftNibble),
            DecodeBatteryNibble(rightNibble),
            DecodeBatteryNibble(caseNibble),
            leftInEar,
            rightInEar,
            flipped ? (flags & 0x02) != 0 : (flags & 0x01) != 0,
            flipped ? (flags & 0x01) != 0 : (flags & 0x02) != 0,
            (flags & 0x04) != 0,
            ((data[8] >> 3) & 0x01) == 0,
            ColorNames.GetValueOrDefault(data[9], "Unknown"),
            ConnectionStates.GetValueOrDefault(data[10], $"Unknown ({data[10]})"),
            modelId);
    }

    private static (bool Charging, int Level) DecodeEncryptedBattery(byte value) =>
        ((value & 0x80) != 0, value & 0x7F);

    private static int? DecodeBatteryNibble(int nibble) =>
        nibble switch
        {
            >= 0x0 and <= 0x9 => nibble * 10,
            >= 0xA and <= 0xE => 100,
            0xF => null,
            _ => null,
        };

    private static byte[]? TryDecryptLastBlock(ReadOnlySpan<byte> data, byte[] key)
    {
        if (data.Length < BluetoothCryptography.BlockSize)
        {
            return null;
        }

        try
        {
            return BluetoothCryptography.DecryptBlock(
                key,
                data[^BluetoothCryptography.BlockSize..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
