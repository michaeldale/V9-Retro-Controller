using System.Net;
using System.Net.Sockets;
using V9Retro.Core;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;
using V9Retro.Protocol.Tests;
using V9Retro.Protocol.Wire;
using V9Retro.Remote.Client;
using V9Retro.Remote.Server;

namespace V9Retro.Remote.Tests;

/// <summary>A real host (CoreHost + ApiServer over HTTPS on loopback), a real client
/// (RemoteBackend), and the fake agent reached through the host's tunnel.</summary>
public sealed class RemoteTests : IAsyncLifetime
{
    readonly string _hostDir = Path.Combine(Path.GetTempPath(), "v9r-host-" + Guid.NewGuid().ToString("N"));
    readonly string _clientDir = Path.Combine(Path.GetTempPath(), "v9r-client-" + Guid.NewGuid().ToString("N"));
    LocalBackend _host = null!;
    FakeAgent _agent = null!;
    string _code = null!;
    RemoteBackend? _client;

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        TunnelPolicy.AllowLoopback = true;
        _host = new LocalBackend(new CoreHost(_hostDir));
        _host.Server!.Port = FreePort();
        await _host.Server.StartAsync();
        (_, _code) = _host.Server.CreatePairing("test-mac", $"https://127.0.0.1:{_host.Server.Port}");
        _agent = new FakeAgent();
        _agent.Handlers[MessageType.InfoRequest] = (r, _) => r.Send(MessageType.InfoResponse,
            new PayloadWriter().U32(3).U32(1000).U32(0x3FFFFF).U16(9869).U16(0x202).U32(0)
                .String("0.13.0").String("b").String("RETRO").String("4.10.2222 A").String("s").String("w").String("c")
                .String("").U8(1).U8(0).U8(0).U8(0).U32(640).U32(480).U32(16).String("").String("")
                .String("aaaaaaaa-1111-4222-8333-444444444444").ToArray());
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        await _agent.DisposeAsync();
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _hostDir, _clientDir })
            try { Directory.Delete(d, true); } catch (IOException) { }
    }

    async Task<RemoteBackend> Connect()
    {
        Assert.True(PairingCode.TryDecode(_code, out var code));
        return _client = await RemoteBackend.ConnectAsync(code!.Url, code.Fingerprint, code.Token, new ClientConfig());
    }

    static async Task<T> Within<T>(TaskCompletionSource<T> tcs) =>
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Client_adds_and_polls_an_agent_on_the_host()
    {
        var client = await Connect();
        Assert.Equal(Environment.MachineName, client.Hello.Machine);

        var added = new TaskCompletionSource<Agent>();
        client.Feed.AgentAdded += a => added.TrySetResult(a);
        await Task.Delay(300); // event stream connected

        var agent = await client.Agents.AddAsync("Retro", "127.0.0.1", _agent.Port);
        Assert.Equal("Retro", (await Within(added)).Name);
        Assert.NotNull(_host.Core.Agents.Get(agent.Id));
        Assert.Contains("test-mac (remote)", (await client.Agents.EventsAsync(agent.Id))[0].Message);

        var poll = await client.Agents.PollAsync(agent.Id);
        Assert.True(poll.Ok);
        Assert.Equal("aaaaaaaa-1111-4222-8333-444444444444", poll.Agent.InstallId);
        Assert.Equal(AgentStatus.Online, _host.Core.Agents.Get(agent.Id)!.Status);
    }

    [Fact]
    public async Task Sessions_run_through_the_tunnel()
    {
        var client = await Connect();
        var info = await client.Agents.Connector.UseAsync("127.0.0.1", _agent.Port, s => s.InfoAsync());
        Assert.Equal("RETRO", info.ComputerName);
        var test = await AgentOps.TestConnectionAsync(client.Agents.Connector, "127.0.0.1", _agent.Port);
        Assert.Equal("0.13.0", test.Info.AgentVersion);
    }

    [Fact]
    public async Task Tunnel_refuses_public_addresses_and_reports_connect_failures()
    {
        var client = await Connect();
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.Agents.Connector.UseAsync("8.8.8.8", 9869, s => s.PingAsync()));
        Assert.Contains("private LAN", refused.Message);
        var closed = await Assert.ThrowsAsync<V9xTransportException>(() => client.Agents.Connector.UseAsync("127.0.0.1", 1, s => s.PingAsync()));
        Assert.Contains("from the host", closed.Message);
    }

    [Fact]
    public async Task Guest_errors_keep_their_status()
    {
        _agent.Handlers[MessageType.RebootRequest] = (r, _) => r.Error(V9xStatus.Busy, 0, "slot active");
        var client = await Connect();
        var a = await client.Agents.AddAsync("Retro", "127.0.0.1", _agent.Port);
        await client.Agents.PollAsync(a.Id);
        var ex = await Assert.ThrowsAsync<V9xGuestException>(() => client.Agents.PowerAsync(a.Id, PowerAction.Reboot));
        Assert.Equal(V9xStatus.Busy, ex.Status);
    }

    [Fact]
    public async Task Settings_are_shared_but_server_keys_are_not()
    {
        var client = await Connect();
        client.Settings.Set(AppSettings.PollMinutes, 7);
        for (var i = 0; i < 50 && _host.Core.Settings.Get(AppSettings.PollMinutes) != 7; i++) await Task.Delay(50);
        Assert.Equal(7, _host.Core.Settings.Get(AppSettings.PollMinutes));

        var api = new ApiClient($"https://127.0.0.1:{_host.Server!.Port}", _host.Server.Fingerprint, Token());
        var shared = await api.GetAsync<Dictionary<string, string>>("api/settings");
        Assert.DoesNotContain(shared.Keys, k => k.StartsWith("api_") || k.StartsWith("server_"));
        await Assert.ThrowsAsync<ArgumentException>(() => api.SendAsync(HttpMethod.Put, "api/settings/server_enabled", new TextValue("0")));
    }

    string Token()
    {
        PairingCode.TryDecode(_code, out var code);
        return code!.Token;
    }

    [Fact]
    public async Task Bad_token_and_wrong_certificate_are_refused()
    {
        var url = $"https://127.0.0.1:{_host.Server!.Port}";
        await Assert.ThrowsAsync<RemoteAuthException>(() =>
            RemoteBackend.ConnectAsync(url, _host.Server.Fingerprint, "v9rt_nope", new ClientConfig()));
        await Assert.ThrowsAsync<RemoteUnavailableException>(() =>
            RemoteBackend.ConnectAsync(url, new string('A', 64), Token(), new ClientConfig()));

        var entry = _host.Server.Tokens.All().Single();
        _host.Server.Tokens.Revoke(entry.Id);
        await Assert.ThrowsAsync<RemoteAuthException>(() => RemoteBackend.ConnectAsync(url, _host.Server.Fingerprint, Token(), new ClientConfig()));
    }

    [Fact]
    public void Pairing_code_round_trips_and_rejects_junk()
    {
        var code = new PairingCode("https://10.0.0.2:9870", "ABCD", "v9rt_x");
        Assert.True(PairingCode.TryDecode(code.Encode(), out var back));
        Assert.Equal(code, back);
        Assert.False(PairingCode.TryDecode("hello", out _));
        Assert.False(PairingCode.TryDecode("v9r1.!!!", out _));
    }

    [Fact]
    public void Tunnel_policy_allows_only_private_ranges()
    {
        TunnelPolicy.AllowLoopback = false;
        try
        {
            Assert.True(TunnelPolicy.IsAllowed(IPAddress.Parse("10.0.1.172")));
            Assert.True(TunnelPolicy.IsAllowed(IPAddress.Parse("192.168.1.5")));
            Assert.True(TunnelPolicy.IsAllowed(IPAddress.Parse("172.20.0.1")));
            Assert.False(TunnelPolicy.IsAllowed(IPAddress.Parse("172.32.0.1")));
            Assert.False(TunnelPolicy.IsAllowed(IPAddress.Parse("8.8.8.8")));
            Assert.False(TunnelPolicy.IsAllowed(IPAddress.Loopback));
        }
        finally
        {
            TunnelPolicy.AllowLoopback = true;
        }
    }
}
