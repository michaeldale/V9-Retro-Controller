using System.Text;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Core.Updates;
using V9Retro.Protocol;
using V9Retro.Protocol.Tests;
using V9Retro.Protocol.Wire;

namespace V9Retro.Core.Tests;

/// <summary>The agent updating itself from the update server: SETUP.EXE /update /S over EXEC,
/// the result file, then a hot swap of the staged binaries.</summary>
public sealed class AgentServerUpdateTests : IAsyncLifetime
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "v9r-agentupd-" + Guid.NewGuid().ToString("N"));
    CoreHost _host = null!;
    FakeAgent _agent = null!;

    public Task InitializeAsync()
    {
        _host = new CoreHost(_dir);
        _agent = new FakeAgent();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _agent.DisposeAsync();
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    Agent Target(string version = "0.13.1") => new()
    {
        Name = "retro", Host = "127.0.0.1", Port = _agent.Port, AgentVersion = version, WindowsVersion = "4.10.2222 A",
        Capabilities = (long)(Capabilities)0x3FFFFF, Status = AgentStatus.Online,
    };

    static byte[] Complete(int exitCode) =>
        new PayloadWriter().U32(0).S32(exitCode).U32(0).U32(900).U32(0).U32(0).U32(0x20).ToArray();

    void ServeSetup(int exitCode, string ini, List<string>? commands = null)
    {
        _agent.Handlers[MessageType.ExecRequest] = async (r, f) =>
        {
            commands?.Add(Encoding.ASCII.GetString(f.Payload));
            await r.Send(MessageType.ExecAccepted, new PayloadWriter().U32(0).ToArray());
            await r.Send(MessageType.ExecComplete, Complete(exitCode));
        };
        var bytes = Encoding.ASCII.GetBytes(ini);
        _agent.Handlers[MessageType.FileOpenRead] = async (r, _) =>
        {
            await r.Send(MessageType.FileReadChunk, new PayloadWriter().U32(0).Bytes(bytes).ToArray());
            await r.Send(MessageType.FileReadComplete, new PayloadWriter().U32((uint)bytes.Length).U32(Crc32.Compute(bytes)).ToArray());
        };
    }

    [Fact]
    public async Task Staged_update_is_hot_swapped_in()
    {
        var commands = new List<string>();
        ServeSetup(0, "[result]\r\nstatus=staged\r\ninstalled=0.13.1\r\nlatest=0.14.0\r\nmessage=staged\r\n", commands);
        var hashed = new List<string>();
        _agent.Handlers[MessageType.FileHashRequest] = (r, f) =>
        {
            hashed.Add(Encoding.ASCII.GetString(f.Payload, 2, f.Payload.Length - 2));
            return r.Send(MessageType.FileHashResponse, new PayloadWriter().U32(1234).U32(0xABCD).Bytes(new byte[32]).ToArray());
        };
        uint[]? applied = null;
        _agent.Handlers[MessageType.UpdateRequest] = (r, f) =>
        {
            applied = Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(f.Payload, i * 4)).ToArray();
            _agent.BootCounter = 8; // the swapped agent comes back with a new boot counter
            return r.Send(MessageType.UpdateAccepted, new PayloadWriter().U32(7).U32(1234).ToArray());
        };

        var result = await _host.Maintenance.UpdateAgentFromServerOnGuestAsync(Target());
        Assert.Contains(commands, c => c.Contains("SETUP.EXE") && c.Contains("/update /S"));
        Assert.Equal([@"C:\V9XREMOTE\V9XNEW.EXE", @"C:\V9XREMOTE\V9XSNEW.EXE"], hashed);
        Assert.NotNull(applied);
        Assert.Equal([1234u, 0xABCDu, 1234u, 0xABCDu], applied!);
        Assert.Equal(7u, result.PreviousBoot);
        Assert.Equal(8u, result.Boot);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Nothing_newer_reports_the_agents_message()
    {
        ServeSetup(3, "[result]\r\nstatus=current\r\nmessage=This is the newest version of the V9x Remote Agent.\r\n");
        var result = await _host.Maintenance.UpdateAgentFromServerOnGuestAsync(Target());
        Assert.Equal("This is the newest version of the V9x Remote Agent.", result.Message);
        Assert.DoesNotContain(_agent.Received, f => f.Type == MessageType.UpdateRequest);
    }

    [Fact]
    public async Task Failure_surfaces_the_reason_and_applies_nothing()
    {
        ServeSetup(1, "[result]\r\nstatus=failed\r\nmessage=The release file's signature is not valid. Nothing was downloaded or changed.\r\n");
        var ex = await Assert.ThrowsAsync<V9xException>(() => _host.Maintenance.UpdateAgentFromServerOnGuestAsync(Target()));
        Assert.Contains("signature is not valid", ex.Message);
        Assert.DoesNotContain(_agent.Received, f => f.Type == MessageType.UpdateRequest);
    }

    [Fact]
    public async Task Older_agents_are_sent_through_the_pc_instead()
    {
        Assert.False(MaintenanceService.CanSelfUpdate(Target("0.13.0")));
        Assert.True(MaintenanceService.CanSelfUpdate(Target("0.13.1")));
        Assert.False(MaintenanceService.CanSelfUpdate(new Agent { AgentVersion = "0.14.0", WindowsVersion = "DOS 6.22", Capabilities = 0x3FFFFF }));
        await Assert.ThrowsAsync<V9xUnsupportedException>(() => _host.Maintenance.UpdateAgentFromServerOnGuestAsync(Target("0.13.0")));
    }

    /// <summary>Against michaeldale.com.au itself; set V9RETRO_LIVE=1 to run.</summary>
    [Fact]
    public async Task Live_server_offers_a_verified_agent_update_to_an_old_agent()
    {
        if (Environment.GetEnvironmentVariable("V9RETRO_LIVE") != "1") return;
        var (check, update) = await MaintenanceService.CheckAgentUpdateAsync(Target("0.12.0"));
        Assert.Equal("update", check.Status);
        Assert.NotNull(update);
        Assert.Equal(check.Latest, update!.Version);
        Assert.StartsWith("v9x-remote-agent-", update.Package.File);
    }

    [Fact]
    public void The_real_agent_release_parses_with_its_own_section()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-0.13.0-SIGNED.TXT"));
        var release = UpdateClient.VerifySignedRelease(file, Convert.FromHexString(UpdateClient.ReleasePublicKeyHex),
            UpdateClient.AgentReleaseSection);
        Assert.Equal("v9x-remote-agent", release.App);
        Assert.Equal("0.13.0", release.Version);
        Assert.Equal(109213, release.Families["win32"].Size);
    }
}
