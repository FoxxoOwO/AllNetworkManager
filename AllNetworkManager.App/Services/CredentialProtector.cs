using System.Security.Cryptography;
using System.Text;

namespace AllNetworkManager.App.Services;

/// <summary>
/// Protects stored device passwords with Windows DPAPI (current user scope), so the inventory
/// file never carries plaintext secrets and cannot be decrypted by another account or machine.
/// </summary>
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AllNetworkManager.DeviceCredentials.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            return null;
        }
    }

    public static string Unprotect(string? base64Cipher)
    {
        if (string.IsNullOrEmpty(base64Cipher)) return "";

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(base64Cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return "";
        }
    }
}
