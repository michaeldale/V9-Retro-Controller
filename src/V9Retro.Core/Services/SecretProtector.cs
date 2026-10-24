using System.Security.Cryptography;
using System.Text;

namespace V9Retro.Core.Services;

/// <summary>Protects stored secrets (VNC passwords). Values are tagged so the scheme can change.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string? stored);
}

/// <summary>DPAPI (current user) on Windows. Elsewhere values are only encoded until a
/// Keychain-backed protector is added for macOS.</summary>
public sealed class DefaultSecretProtector : ISecretProtector
{
    static readonly byte[] Entropy = "V9RetroController.v1"u8.ToArray();

    public string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        return "b64:" + Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy,
                    DataProtectionScope.CurrentUser));
            if (stored.StartsWith("b64:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[4..]));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Another user's or machine's DPAPI blob: treat as no password.
        }
        return null;
    }
}
