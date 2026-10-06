using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FluenTTY.Services;

/// <summary>
/// Protects saved passwords. At rest they are DPAPI blobs bound to the current Windows user
/// ("dpapi:" prefix), so settings files copied to another machine or account can't reveal them.
/// For export they are re-encrypted with a passphrase the user chooses (AES-256-GCM, PBKDF2-SHA256).
/// </summary>
public static class SecretStore
{
    private const string DpapiPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FluenTTY saved secret v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = Encoding.UTF8.GetBytes(plain);
        var blob = Dpapi(data, protect: true);
        CryptographicOperations.ZeroMemory(data);
        return DpapiPrefix + Convert.ToBase64String(blob);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var plain = Dpapi(Convert.FromBase64String(stored[DpapiPrefix.Length..]), protect: false);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"could not unprotect a saved secret: {ex.Message}");
            return null;
        }
    }

    public static bool IsProtected(string? stored) => stored?.StartsWith(DpapiPrefix, StringComparison.Ordinal) == true;

    // ------------------------------------------------------------------ export encryption

    public static string EncryptForExport(string plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 210_000, HashAlgorithmName.SHA256, 32);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, data, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return "aesgcm:" + Convert.ToBase64String(salt.Concat(nonce).Concat(tag).Concat(cipher).ToArray());
    }

    public static string? DecryptFromExport(string blob, string passphrase)
    {
        if (!blob.StartsWith("aesgcm:", StringComparison.Ordinal)) return null;
        try
        {
            var raw = Convert.FromBase64String(blob[7..]);
            var salt = raw[..16];
            var nonce = raw[16..28];
            var tag = raw[28..44];
            var cipher = raw[44..];
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 210_000, HashAlgorithmName.SHA256, 32);
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
            CryptographicOperations.ZeroMemory(key);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ DPAPI (crypt32) without the extra package

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static byte[] Dpapi(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { cbData = Entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            var dataOut = new DataBlob();
            bool ok = protect
                ? CryptProtectData(ref dataIn, "FluenTTY", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
                return result;
            }
            finally
            {
                LocalFree(dataOut.pbData);
            }
        }
        finally
        {
            inHandle.Free();
            entHandle.Free();
        }
    }
}
