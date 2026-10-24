using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using V9Retro.Core.Data;
using V9Retro.Core.Services;

namespace V9Retro.Remote.Server;

public sealed record ApiToken(string Id, string Name, string Hash, long CreatedAt, long? LastUsedAt);

/// <summary>API tokens, kept as SHA-256 hashes in the settings table. A token is shown once when
/// created; revoking it cuts off that client at its next request.</summary>
public sealed class TokenStore(SettingsStore settings)
{
    const string Key = "api_tokens";
    readonly object _gate = new();

    public IReadOnlyList<ApiToken> All()
    {
        lock (_gate) return Load();
    }

    List<ApiToken> Load() =>
        settings.Get(Key) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<ApiToken>>(json, ApiJson.Options) ?? [] : [];

    void Save(List<ApiToken> tokens) => settings.Set(Key, JsonSerializer.Serialize(tokens, ApiJson.Options));

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Creates a token and returns it in plain text (the only time it is visible).</summary>
    public (ApiToken Entry, string Token) Create(string name)
    {
        var token = "v9rt_" + PairingCode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var entry = new ApiToken(Guid.NewGuid().ToString("N")[..12], name.Trim().Length > 0 ? name.Trim() : "client",
            Hash(token), Clock.Now(), null);
        lock (_gate)
        {
            var all = Load();
            all.Add(entry);
            Save(all);
        }
        return (entry, token);
    }

    public void Revoke(string id)
    {
        lock (_gate)
        {
            var all = Load();
            all.RemoveAll(t => t.Id == id);
            Save(all);
        }
    }

    /// <summary>The token's entry, or null. Last-used is written at most once a minute.</summary>
    public ApiToken? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        lock (_gate)
        {
            var all = Load();
            for (var i = 0; i < all.Count; i++)
            {
                if (!CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(all[i].Hash))) continue;
                var now = Clock.Now();
                if (all[i].LastUsedAt is null || now - all[i].LastUsedAt > 60_000)
                {
                    all[i] = all[i] with { LastUsedAt = now };
                    Save(all);
                }
                return all[i];
            }
        }
        return null;
    }
}

/// <summary>The host's self-signed TLS certificate, made on first use and kept in the data folder
/// as a PFX whose password is protected in the settings table. Clients pin its SHA-256 fingerprint.</summary>
public static class CertificateStore
{
    const string PasswordKey = "server_cert_password";

    public static X509Certificate2 LoadOrCreate(string dataDirectory, SettingsStore settings, ISecretProtector secrets)
    {
        var path = Path.Combine(dataDirectory, "server-cert.pfx");
        var password = secrets.Unprotect(settings.Get(PasswordKey));
        if (File.Exists(path) && password is not null)
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30)) return existing;
            }
            catch (CryptographicException)
            {
                // Unreadable (another user's DPAPI, or corrupt): make a new one; clients re-pair.
            }
        }

        password = PairingCode.Base64Url(RandomNumberGenerator.GetBytes(24));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=V9 Retro Controller ({Environment.MachineName})", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        foreach (var ip in LocalAddresses()) san.AddIpAddress(ip);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx, password));
        settings.Set(PasswordKey, secrets.Protect(password));
        // Reload from PFX so the key is persisted (SChannel cannot use an ephemeral key).
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>Up IPv4 addresses of this machine, LAN ones first.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderBy(n => n.GetIPProperties().GatewayAddresses.Count > 0 ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();
}

/// <summary>Which addresses the tunnel may open connections to: the private LAN ranges only,
/// so a client token cannot turn the host into a proxy to the internet or to the host's own
/// local services.</summary>
public static class TunnelPolicy
{
    /// <summary>Tests run the fake agent on loopback.</summary>
    internal static bool AllowLoopback { get; set; }

    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return AllowLoopback;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT / Tailscale
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }
        return false;
    }
}
