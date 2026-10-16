using System.Security.Cryptography;
using System.Text;

namespace Sandbox.Shared;

/// <summary>
/// Sandbox-only cipher: AES-CBC (PKCS7) with a fixed dummy key and IV, base64 on the wire.
/// Unauthenticated and trivially reproducible on purpose. Never reuse it.
/// </summary>
public static class PayloadCipher
{
    private static readonly byte[] Key = "sandbox-dummy-key-do-not-reuse!!"u8.ToArray();
    private static readonly byte[] Iv = "sandbox-iv-00000"u8.ToArray();

    public static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), Iv, PaddingMode.PKCS7));
    }

    public static bool TryDecrypt(string payload, out string plaintext)
    {
        plaintext = string.Empty;
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            var cipher = Convert.FromBase64String(payload);
            plaintext = Encoding.UTF8.GetString(aes.DecryptCbc(cipher, Iv, PaddingMode.PKCS7));
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
