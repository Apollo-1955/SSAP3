using System.Globalization;
using System.Security.Cryptography;

namespace SSAP3.Core;

// Resolvable-private-address check adapted from the GPL-3.0 LibrePods Windows
// port. The AES usage below is the Bluetooth Core Specification security
// function "e" (Vol 3, Part H, 2.2.1): AES-128 applied to exactly one block.
public static class BluetoothCryptography
{
    public const int BlockSize = 16;

    public static bool VerifyResolvablePrivateAddress(
        string address,
        byte[] identityResolvingKey)
    {
        ArgumentNullException.ThrowIfNull(identityResolvingKey);
        if (identityResolvingKey.Length != BlockSize)
        {
            return false;
        }

        if (!TryParseMacAddress(address, out var addressBytes))
        {
            return false;
        }

        Array.Reverse(addressBytes);
        var randomPart = addressBytes[3..6];
        var hash = addressBytes[0..3];
        var computedHash = Ah(identityResolvingKey, randomPart);
        return CryptographicOperations.FixedTimeEquals(hash, computedHash);
    }

    public static byte[] Ah(byte[] key, byte[] randomPart)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(randomPart);
        var paddedRandom = new byte[BlockSize];
        Buffer.BlockCopy(randomPart, 0, paddedRandom, 0, Math.Min(randomPart.Length, 3));
        var swappedKey = Enumerable.Reverse(key).ToArray();
        var swappedData = Enumerable.Reverse(paddedRandom).ToArray();
        var encrypted = EncryptBlock(swappedKey, swappedData);
        Array.Reverse(encrypted);
        return encrypted[..3];
    }

    public static bool TryParseMacAddress(string? address, out byte[] octets)
    {
        octets = [];
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var parts = address.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 6)
        {
            return false;
        }

        var parsed = new byte[6];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length != 2 ||
                !byte.TryParse(
                    parts[index],
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out parsed[index]))
            {
                return false;
            }
        }

        octets = parsed;
        return true;
    }

    public static byte[] ParseMacAddress(string address) =>
        TryParseMacAddress(address, out var octets)
            ? octets
            : throw new ArgumentException(
                "Bluetooth address must contain six hexadecimal octets.",
                nameof(address));

    /// <summary>AES-128 encryption of exactly one 16-byte block.</summary>
    public static byte[] EncryptBlock(byte[] key, ReadOnlySpan<byte> block) =>
        TransformBlock(key, block, encrypt: true);

    /// <summary>AES-128 decryption of exactly one 16-byte block.</summary>
    public static byte[] DecryptBlock(byte[] key, ReadOnlySpan<byte> block) =>
        TransformBlock(key, block, encrypt: false);

    private static byte[] TransformBlock(byte[] key, ReadOnlySpan<byte> block, bool encrypt)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != BlockSize)
        {
            throw new ArgumentException("AES-128 keys are 16 bytes.", nameof(key));
        }

        if (block.Length != BlockSize)
        {
            throw new ArgumentException("Exactly one 16-byte block is required.", nameof(block));
        }

        using var aes = Aes.Create();
        aes.Key = key;
        // CA5358: a single block with no padding is the raw AES permutation that
        // the Bluetooth "e" function and Apple's 16-byte advertisement payload
        // require. No multi-block plaintext is ever processed, so ECB's pattern
        // leakage does not apply.
#pragma warning disable CA5358
        return encrypt
            ? aes.EncryptEcb(block, PaddingMode.None)
            : aes.DecryptEcb(block, PaddingMode.None);
#pragma warning restore CA5358
    }
}
