using System.Net;
using System.Text;
using V9Retro.Core.Updates;

namespace V9Retro.Core.Tests;

public class UpdateTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    static readonly string TestKey = File.ReadAllText(Fixture("test-public.txt")).Trim();

    [Theory]
    // RFC 8032 section 7.1, tests 1 and 2.
    [InlineData("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    public void Ed25519_rfc8032_vectors(string publicKey, string message, string signature)
    {
        var pk = Convert.FromHexString(publicKey);
        var msg = Convert.FromHexString(message);
        var sig = Convert.FromHexString(signature);
        Assert.True(Ed25519.Verify(pk, msg, sig));
        sig[5] ^= 1;
        Assert.False(Ed25519.Verify(pk, msg, sig));
        Assert.False(Ed25519.Verify(pk, [.. msg, 0], Convert.FromHexString(signature)));
    }

    [Fact]
    public void The_real_agent_release_verifies_under_the_shared_release_key()
    {
        var file = File.ReadAllBytes(Fixture("agent-0.13.0-SIGNED.TXT"));
        var text = Encoding.ASCII.GetString(file);
        var marker = text.IndexOf("[Signature]", StringComparison.Ordinal);
        var sig = Convert.FromHexString(text[(marker + "[Signature]\r\nEd25519=".Length)..].Trim());
        Assert.True(Ed25519.Verify(Convert.FromHexString(UpdateClient.ReleasePublicKeyHex), file.AsSpan(0, marker), sig));
    }

    [Fact]
    public void Signed_release_parses_and_rejects_tampering()
    {
        var file = File.ReadAllBytes(Fixture("test-SIGNED.TXT"));
        var release = UpdateClient.VerifySignedRelease(file, Convert.FromHexString(TestKey));
        Assert.Equal("v9-retro-controller", release.App);
        Assert.Equal("0.2.0", release.Version);
        Assert.Equal(4200, release.Families["win-x64"].Size);

        var tampered = (byte[])file.Clone();
        tampered[Encoding.ASCII.GetString(file).IndexOf("0.2.0", StringComparison.Ordinal)] = (byte)'9';
        Assert.Throws<UpdateVerificationException>(() => UpdateClient.VerifySignedRelease(tampered, Convert.FromHexString(TestKey)));

        var appended = file.Concat(Encoding.ASCII.GetBytes("[Family.osx-arm64]\r\nFile=evil.zip\r\n")).ToArray();
        Assert.Throws<UpdateVerificationException>(() => UpdateClient.VerifySignedRelease(appended, Convert.FromHexString(TestKey)));

        Assert.Throws<UpdateVerificationException>(() =>
            UpdateClient.VerifySignedRelease(file, Convert.FromHexString(UpdateClient.ReleasePublicKeyHex)));
    }

    [Fact]
    public void Versions_compare_numerically()
    {
        Assert.Equal(1, UpdateClient.CompareVersions("0.10.0", "0.9.9"));
        Assert.Equal(0, UpdateClient.CompareVersions("v1.2", "1.2.0"));
        Assert.Equal(-1, UpdateClient.CompareVersions("junk", "0.1.0"));
    }

    /// <summary>Serves the check reply, the signed file and the package like the plugin does.</summary>
    sealed class FakeServer(Func<string, string> check, byte[] signed, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            HttpContent content = path.StartsWith("/v9update/check", StringComparison.Ordinal) ? new StringContent(check(path))
                : path.StartsWith("/v9update/signed/", StringComparison.Ordinal) ? new ByteArrayContent(signed)
                : path.StartsWith("/v9update/notes/", StringComparison.Ordinal) ? new StringContent("Release notes")
                : path.StartsWith("/v9update/get/", StringComparison.Ordinal) ? new ByteArrayContent(package)
                : new StringContent("");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    static string Reply(string sha, long size, string latest = "0.2.0") =>
        "[update]\r\nstatus=update\r\napp=v9-retro-controller\r\ninstalled=0.1.0\r\nlatest=" + latest + "\r\n" +
        "notes=http://u.test/v9update/notes/v9-retro-controller/0.2.0\r\nsigned=http://u.test/v9update/signed/v9-retro-controller/0.2.0\r\n\r\n" +
        $"[package]\r\nfamily=win-x64\r\nfile=v9-retro-controller-0.2.0-win-x64.zip\r\nurl=http://u.test/v9update/get/9/v9-retro-controller-0.2.0-win-x64.zip\r\nsize={size}\r\nsha256={sha}\r\n";

    [Fact]
    public async Task Check_verify_and_download_end_to_end()
    {
        var package = File.ReadAllBytes(Fixture("v9-retro-controller-0.2.0-win-x64.zip"));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)).ToLowerInvariant();
        var signed = File.ReadAllBytes(Fixture("test-SIGNED.TXT"));
        string? asked = null;
        using var client = new UpdateClient("http://u.test", new FakeServer(q => { asked = q; return Reply(sha, package.Length); }, signed, package), TestKey);

        var check = await client.CheckAsync("0.1.0", "win-x64");
        Assert.Equal("/v9update/check?app=v9-retro-controller&family=win-x64&version=0.1.0", asked);
        Assert.True(check.UpdateAvailable);
        var update = await client.VerifyAsync(check, "0.1.0", "win-x64");
        Assert.Equal("0.2.0", update.Version);
        Assert.Equal("Release notes", update.Notes);

        var dir = Path.Combine(Path.GetTempPath(), "v9r-upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = await client.DownloadAsync(update, dir);
            Assert.Equal(package, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Mismatches_stop_before_anything_is_installed()
    {
        var package = File.ReadAllBytes(Fixture("v9-retro-controller-0.2.0-win-x64.zip"));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)).ToLowerInvariant();
        var signed = File.ReadAllBytes(Fixture("test-SIGNED.TXT"));

        // Server offers a different hash than the signed file.
        using (var c = new UpdateClient("http://u.test", new FakeServer(_ => Reply(new string('0', 64), package.Length), signed, package), TestKey))
            await Assert.ThrowsAsync<UpdateVerificationException>(async () => await c.VerifyAsync(await c.CheckAsync("0.1.0", "win-x64"), "0.1.0", "win-x64"));

        // latest= disagrees with the signed version.
        using (var c = new UpdateClient("http://u.test", new FakeServer(_ => Reply(sha, package.Length, "0.3.0"), signed, package), TestKey))
            await Assert.ThrowsAsync<UpdateVerificationException>(async () => await c.VerifyAsync(await c.CheckAsync("0.1.0", "win-x64"), "0.1.0", "win-x64"));

        // Already on that version (downgrade/replay protection uses the signed version).
        using (var c = new UpdateClient("http://u.test", new FakeServer(_ => Reply(sha, package.Length), signed, package), TestKey))
            await Assert.ThrowsAsync<UpdateVerificationException>(async () => await c.VerifyAsync(await c.CheckAsync("0.2.0", "win-x64"), "0.2.0", "win-x64"));

        // Package bytes changed after signing.
        var evil = (byte[])package.Clone();
        evil[0] ^= 0xFF;
        using (var c = new UpdateClient("http://u.test", new FakeServer(_ => Reply(sha, package.Length), signed, evil), TestKey))
        {
            var update = await c.VerifyAsync(await c.CheckAsync("0.1.0", "win-x64"), "0.1.0", "win-x64");
            var dir = Path.Combine(Path.GetTempPath(), "v9r-upd-" + Guid.NewGuid().ToString("N"));
            await Assert.ThrowsAsync<UpdateVerificationException>(() => c.DownloadAsync(update, dir));
            Assert.Empty(Directory.GetFiles(dir, "*.zip"));
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Statuses_without_a_package_are_not_updates()
    {
        var check = UpdateClient.ParseCheck("[update]\r\nstatus=no_release\r\nmessage=No release published yet.\r\n", "0.1.0");
        Assert.False(check.UpdateAvailable);
        Assert.Equal("No release published yet.", check.Message);
        Assert.Equal("current", UpdateClient.ParseCheck("[update]\r\nstatus=current\r\nlatest=0.1.0\r\n", "0.1.0").Status);
    }
}
