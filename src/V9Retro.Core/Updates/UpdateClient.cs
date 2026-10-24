using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace V9Retro.Core.Updates;

/// <summary>One package the server offers for this platform.</summary>
public sealed record UpdatePackage(string Family, string File, string Url, long Size, string Sha256);

/// <summary>The server's answer to a check (the [update] and [package] sections).</summary>
public sealed record UpdateCheck(string Status, string Installed, string? Latest, string? Released, string? NotesUrl,
    string? SignedUrl, string? Message, UpdatePackage? Package)
{
    public bool UpdateAvailable => Status == "update" && Latest is not null && Package is not null;
}

/// <summary>An update whose signed release file verified and agrees with the check.</summary>
public sealed record VerifiedUpdate(UpdateCheck Check, string Version, UpdatePackage Package, string Notes);

/// <summary>A verification step failed; nothing should be installed.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>Checks the michaeldale.com.au update server (the v9x_update_checker plugin) for a newer
/// V9 Retro Controller, the same way the Velocity9x updater and the agent's SETUP.EXE /update do:
/// <list type="number">
/// <item>GET /v9update/check?app=v9-retro-controller&amp;family=&lt;rid&gt;&amp;version=&lt;this&gt;.</item>
/// <item>On status=update, fetch the signed= file (SIGNED.TXT) and verify its Ed25519 signature
/// against the release key the agent uses (<see cref="ReleasePublicKeyHex"/>).</item>
/// <item>Require the signed version to equal latest= and be newer than this one, and its entry for
/// this family to name the offered file with the same size and SHA-256.</item>
/// <item>Download the zip and check its size and SHA-256 against the signed file.</item>
/// </list>
/// The server, transport and sequence numbers are not trusted; only the signature is.</summary>
public sealed class UpdateClient : IDisposable
{
    public const string App = "v9-retro-controller";
    public const string DefaultServer = "https://michaeldale.com.au";
    public const string ReleaseSection = "V9RetroRelease";

    /// <summary>The agent's own entry on the same server (SETUP.EXE /update asks for this).</summary>
    public const string AgentApp = "v9x-remote-agent";
    public const string AgentFamily = "win32";
    public const string AgentReleaseSection = "V9xRemoteRelease";

    /// <summary>The Ed25519 public key that signs releases: the same key as V9x Remote Agent
    /// (include\v9xremote\release_key.h). Its private half lives only in
    /// %USERPROFILE%\.v9x\release-signing.key on the developer's PC.</summary>
    public const string ReleasePublicKeyHex = "98406c9634648a373fd8a14744c92ab826151983ccaef08db41b037cdadef317";

    readonly HttpClient _http;
    readonly string _server;
    readonly byte[] _publicKey;
    readonly string _app;
    readonly string _section;

    /// <param name="app">The app slug to ask about: this app by default, or <see cref="AgentApp"/>.</param>
    /// <param name="releaseSection">The signed file's first section for that app.</param>
    public UpdateClient(string? server = null, HttpMessageHandler? handler = null, string? publicKeyHex = null,
        string app = App, string releaseSection = ReleaseSection)
    {
        _app = app;
        _section = releaseSection;
        _server = (server ?? Environment.GetEnvironmentVariable("V9RETRO_UPDATE_SERVER") ?? DefaultServer).TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"V9RetroController/{CurrentVersion}");
        _publicKey = Convert.FromHexString(publicKeyHex ?? ReleasePublicKeyHex);
    }

    /// <summary>This build's version (Directory.Build.props), three parts.</summary>
    public static string CurrentVersion =>
        typeof(UpdateClient).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "0.0.0";

    /// <summary>The package family for this machine: win-x64, win-arm64, osx-arm64 or osx-x64.</summary>
    public static string Family
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };
            return $"{os}-{arch}";
        }
    }

    public async Task<UpdateCheck> CheckAsync(string? installed = null, string? family = null, CancellationToken ct = default)
    {
        installed ??= CurrentVersion;
        family ??= Family;
        var url = $"{_server}/v9update/check?app={Uri.EscapeDataString(_app)}&family={Uri.EscapeDataString(family)}&version={Uri.EscapeDataString(installed)}";
        var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseCheck(text, installed);
    }

    internal static UpdateCheck ParseCheck(string text, string installed)
    {
        var ini = Ini.Parse(text);
        string? Get(string section, string key) => ini.GetValueOrDefault(section)?.GetValueOrDefault(key);
        UpdatePackage? package = null;
        if (Get("package", "url") is { } url && Get("package", "file") is { } file
            && long.TryParse(Get("package", "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            && Get("package", "sha256") is { Length: 64 } sha)
            package = new UpdatePackage(Get("package", "family") ?? "", file, url, size, sha.ToLowerInvariant());
        return new UpdateCheck(Get("update", "status") ?? "error", Get("update", "installed") ?? installed, Get("update", "latest"),
            Get("update", "released"), Get("update", "notes"), Get("update", "signed"), Get("update", "message"), package);
    }

    /// <summary>Fetches and verifies the signed release file and the release notes. Throws
    /// <see cref="UpdateVerificationException"/> on any mismatch.</summary>
    public async Task<VerifiedUpdate> VerifyAsync(UpdateCheck check, string? installed = null, string? family = null,
        CancellationToken ct = default)
    {
        installed ??= CurrentVersion;
        family ??= Family;
        if (!check.UpdateAvailable || check.SignedUrl is null)
            throw new UpdateVerificationException("the server did not offer a signed update");
        var signed = await _http.GetByteArrayAsync(check.SignedUrl, ct).ConfigureAwait(false);
        var release = VerifySignedRelease(signed, _publicKey, _section);

        if (release.App != _app) throw new UpdateVerificationException($"the signed release is for \"{release.App}\", not {_app}");
        if (release.Version != check.Latest)
            throw new UpdateVerificationException($"the signed version {release.Version} is not the offered {check.Latest}");
        if (CompareVersions(release.Version, installed) <= 0)
            throw new UpdateVerificationException($"the signed version {release.Version} is not newer than {installed}");
        if (!release.Families.TryGetValue(family, out var entry))
            throw new UpdateVerificationException($"the signed release has no package for {family}");
        var offered = check.Package!;
        if (!string.Equals(entry.File, offered.File, StringComparison.OrdinalIgnoreCase) || entry.Size != offered.Size
            || !string.Equals(entry.Sha256, offered.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException("the offered package does not match the signed release");

        var notes = "";
        if (check.NotesUrl is { } notesUrl)
        {
            try { notes = await _http.GetStringAsync(notesUrl, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { /* notes are optional */ }
        }
        return new VerifiedUpdate(check, release.Version, offered with { Sha256 = entry.Sha256 }, notes.Trim());
    }

    /// <summary>Downloads the package into <paramref name="directory"/> and checks its size and SHA-256
    /// against the signed values. Returns the file path.</summary>
    public async Task<string> DownloadAsync(VerifiedUpdate update, string directory, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(update.Package.File);
        if (name.Length == 0 || name != update.Package.File) throw new UpdateVerificationException("bad package file name");
        var path = Path.Combine(directory, name);
        var part = path + ".part";
        using (var response = await _http.GetAsync(update.Package.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(part);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > update.Package.Size) throw new UpdateVerificationException("the download is larger than the signed size");
                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report((double)total / update.Package.Size);
            }
            if (total != update.Package.Size) throw new UpdateVerificationException("the download size does not match the signed release");
            if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), update.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateVerificationException("the download's SHA-256 does not match the signed release");
        }
        File.Move(part, path, overwrite: true);
        return path;
    }

    // ---- signed release file -----------------------------------------------------------

    public sealed record SignedFamily(string File, long Size, string Sha256);

    public sealed record SignedRelease(string App, string Version, string? Build, IReadOnlyDictionary<string, SignedFamily> Families);

    /// <summary>Checks the signature over every byte before the "[Signature]" line, requires exactly
    /// one [Signature] section with nothing after it, then parses only the signed bytes.</summary>
    public static SignedRelease VerifySignedRelease(byte[] file, byte[] publicKey, string section = ReleaseSection)
    {
        var text = Encoding.ASCII.GetString(file);
        var marker = text.IndexOf("[Signature]", StringComparison.Ordinal);
        if (marker <= 0 || (text[marker - 1] != '\n') || text.IndexOf("[Signature]", marker + 1, StringComparison.Ordinal) >= 0)
            throw new UpdateVerificationException("the release file has no single [Signature] section");
        var tail = Ini.Parse(text[marker..]);
        if (tail.Count != 1 || !tail.TryGetValue("signature", out var sig) || sig.Count != 1
            || !sig.TryGetValue("ed25519", out var hex) || hex.Length != 128)
            throw new UpdateVerificationException("the release file's [Signature] section is malformed");
        byte[] signature;
        try { signature = Convert.FromHexString(hex); }
        catch (FormatException) { throw new UpdateVerificationException("the release signature is not hex"); }
        if (!Ed25519.Verify(publicKey, file.AsSpan(0, marker), signature))
            throw new UpdateVerificationException("the release signature does not verify: this is not a genuine release");

        var body = Ini.Parse(text[..marker]);
        if (!body.TryGetValue(section.ToLowerInvariant(), out var head) || head.GetValueOrDefault("schema") != "1")
            throw new UpdateVerificationException($"the release file has no [{section}] schema 1 section");
        var families = new Dictionary<string, SignedFamily>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in body)
        {
            if (!name.StartsWith("family.", StringComparison.Ordinal)) continue;
            if (values.GetValueOrDefault("file") is { } f
                && long.TryParse(values.GetValueOrDefault("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                && values.GetValueOrDefault("sha256") is { Length: 64 } sha)
                families[name["family.".Length..]] = new SignedFamily(f, size, sha.ToLowerInvariant());
        }
        return new SignedRelease(head.GetValueOrDefault("app") ?? "", head.GetValueOrDefault("version") ?? "",
            head.GetValueOrDefault("build"), families);
    }

    /// <summary>-1, 0 or 1 numerically; an unparsable version sorts lowest so it is never "newer".</summary>
    public static int CompareVersions(string? left, string? right)
    {
        static Version? Parse(string? s) =>
            s is not null && Version.TryParse(s.Trim().TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
        var (l, r) = (Parse(left), Parse(right));
        if (l is null) return r is null ? 0 : -1;
        return r is null ? 1 : l.CompareTo(r);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The INI shape the server and release files use: sections and keys compared
    /// without case, first match wins, CR and surrounding spaces trimmed.</summary>
    static class Ini
    {
        public static Dictionary<string, Dictionary<string, string>> Parse(string text)
        {
            var result = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, string>? current = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    var name = line[1..^1].Trim().ToLowerInvariant();
                    if (!result.TryGetValue(name, out current)) result[name] = current = new Dictionary<string, string>();
                    continue;
                }
                var eq = line.IndexOf('=');
                if (current is null || eq <= 0) continue;
                current.TryAdd(line[..eq].Trim().ToLowerInvariant(), line[(eq + 1)..].Trim());
            }
            return result;
        }
    }
}
