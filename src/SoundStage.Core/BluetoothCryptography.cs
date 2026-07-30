using System.Security.Cryptography;

namespace SoundStage.Core;

// Adapted from the GPL-3.0 LibrePods Windows port.
public static class BluetoothCryptography
{
    public static bool VerifyResolvablePrivateAddress(
        string address,
        byte[] identityResolvingKey)
    {
        var addressBytes = ParseMacAddress(address);
        Array.Reverse(addressBytes);

        var randomPart = addressBytes[3..6];
        var hash = addressBytes[0..3];
        var computedHash = Ah(identityResolvingKey, randomPart);
        return hash.SequenceEqual(computedHash);
    }

    public static byte[] Ah(byte[] key, byte[] randomPart)
    {
        var paddedRandom = new byte[16];
        Buffer.BlockCopy(randomPart, 0, paddedRandom, 0, Math.Min(randomPart.Length, 3));
        return Encrypt(key, paddedRandom)[..3];
    }

    public static byte[] ParseMacAddress(string address)
    {
        var parts = address.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 6)
        {
            throw new ArgumentException(
                "Bluetooth address must contain six octets.",
                nameof(address));
        }

        return parts.Select(part => Convert.ToByte(part, 16)).ToArray();
    }

    private static byte[] Encrypt(byte[] key, byte[] data)
    {
        var swappedKey = key.Reverse().ToArray();
        var swappedData = data.Reverse().ToArray();

        using var aes = Aes.Create();
        aes.Key = swappedKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(swappedData, 0, swappedData.Length);
        Array.Reverse(encrypted);
        return encrypted;
    }
}
