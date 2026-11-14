using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SSAP3.Core.Motion.Companion;

namespace SSAP3.Platform.Windows;

/// <summary>
/// Stores the companion pairing key for the signed-in user, encrypted with
/// Windows DPAPI (CurrentUser scope, with SSAP3-specific entropy) in
/// %LocalAppData%\SSAP3\companion-pairing.bin. Only this Windows account can
/// decrypt it. The key never appears in settings.json or logs. The pairing code
/// is shown only when the user creates it.
/// </summary>
public static class CompanionPairingStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const long MaximumFileBytes = 4096;
    private static readonly byte[] Entropy = "SSAP3 companion pairing v1"u8.ToArray();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSAP3", "companion-pairing.bin");

    public static bool Exists => File.Exists(FilePath);

    /// <summary>Returns the 32-byte key (caller zeroes it after use), or null.</summary>
    public static byte[]? Load()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length is 0 or > MaximumFileBytes)
            {
                return null;
            }

            var key = Unprotect(File.ReadAllBytes(FilePath));
            if (key is null || key.Length != CompanionProtocol.KeyLength)
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                return null;
            }

            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Creates and stores a new key, replacing any old pairing. Returns the pairing code to give the companion.</summary>
    public static string CreateNew()
    {
        var key = CompanionProtocol.CreatePairingKey();
        try
        {
            var protectedKey = Protect(key) ?? throw new CryptographicException("Windows could not protect the pairing key.");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, protectedKey);
            File.Move(temporary, FilePath, overwrite: true);
            return CompanionProtocol.FormatPairingCode(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[]? Protect(byte[] data) => Transform(data, protect: true);

    private static byte[]? Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
            var outBlob = default(DataBlob);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, CryptProtectUiForbidden, ref outBlob);
            if (!ok || outBlob.Data == 0)
            {
                return null;
            }

            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                if (!protect)
                {
                    // Clear the decrypted key before releasing the native buffer.
                    for (var index = 0; index < outBlob.Size; index++)
                    {
                        Marshal.WriteByte(outBlob.Data, index, 0);
                    }
                }

                _ = LocalFree(outBlob.Data);
            }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
