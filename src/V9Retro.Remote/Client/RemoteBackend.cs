using System.Net.WebSockets;
using System.Text.Json;
using V9Retro.Core;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Discovery;
using V9Retro.Remote.Server;

namespace V9Retro.Remote.Client;

/// <summary>How this app starts: on its own database, or as a client of a host. Kept next to
/// the local data folder, with the token protected (DPAPI on Windows; file mode 600 elsewhere).</summary>
public sealed class ClientConfig
{
    public string Mode { get; set; } = "local";
    public string? Url { get; set; }
    public string? Fingerprint { get; set; }
    public string? ProtectedToken { get; set; }
    /// <summary>Per-machine preferences that stay local even in remote mode (close to tray).</summary>
    public Dictionary<string, string> LocalSettings { get; set; } = new();

    public bool IsRemote => Mode == "remote" && Url is not null && Fingerprint is not null && ProtectedToken is not null;

    public static string PathFor(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? CoreHost.DefaultDataDirectory(), "client.json");

    public static ClientConfig Load(string? dataDirectory = null)
    {
        try
        {
            var path = PathFor(dataDirectory);
            if (File.Exists(path)) return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), ApiJson.Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new ClientConfig();
    }

    public void Save(string? dataDirectory = null)
    {
        var path = PathFor(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ApiJson.Options));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void UsePairing(PairingCode code, ISecretProtector secrets)
    {
        Mode = "remote";
        Url = code.Url;
        Fingerprint = code.Fingerprint;
        ProtectedToken = secrets.Protect(code.Token);
    }
}

/// <summary>This app as a client of a host: the store, actions and discovery over the host's API,
/// and agent connections through its tunnel. The change feed is fed from the host's event stream,
/// which reconnects by itself.</summary>
public sealed class RemoteBackend : IControllerBackend
{
    readonly ApiClient _api;
    readonly RemoteSettings _settingsBackend;
    readonly RemoteDiscovery _discovery;
    readonly CancellationTokenSource _stop = new();
    Task? _streamLoop;
    bool _connected;

    RemoteBackend(ApiClient api, HelloDto hello, ClientConfig config, Dictionary<string, string> settings)
    {
        _api = api;
        Hello = hello;
        Feed = new ChangeFeed();
        var connector = new AgentConnector(new TunnelTransport(api)) { Options = new() { ClientLabel = "v9retro-remote" } };
        Agents = new RemoteAgentService(api, Feed, connector);
        _settingsBackend = new RemoteSettings(api, config, settings);
        Settings = new AppSettings(_settingsBackend);
        Maintenance = new MaintenanceService(Agents);
        _discovery = new RemoteDiscovery(api);
        Location = $"{hello.Machine} ({api.BaseUri.Host})";
    }

    public HelloDto Hello { get; }
    public IAgentService Agents { get; }
    public AppSettings Settings { get; }
    public MaintenanceService Maintenance { get; }
    public ChangeFeed Feed { get; }
    public IDiscoverySource Discovery => _discovery;
    public bool IsRemote => true;
    public string Location { get; }
    public string? DataDirectory => null;
    public ApiServer? Server => null;
    public event Action<bool>? ConnectionChanged;

    /// <summary>Checks the address, certificate and token, loads settings and discovery, and starts
    /// the event stream. Throws <see cref="RemoteUnavailableException"/> or <see cref="RemoteAuthException"/>.</summary>
    public static async Task<RemoteBackend> ConnectAsync(string url, string fingerprint, string token, ClientConfig config,
        CancellationToken ct = default)
    {
        var api = new ApiClient(url, fingerprint, token);
        try
        {
            var hello = await api.GetAsync<HelloDto>("api/hello", ct).ConfigureAwait(false);
            var settings = await api.GetAsync<Dictionary<string, string>>("api/settings", ct).ConfigureAwait(false);
            var backend = new RemoteBackend(api, hello, config, settings);
            await backend._discovery.LoadAsync(ct).ConfigureAwait(false);
            backend._streamLoop = Task.Run(() => backend.StreamLoopAsync(backend._stop.Token));
            return backend;
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    public static Task<RemoteBackend> ConnectAsync(ClientConfig config, ISecretProtector secrets, CancellationToken ct = default)
    {
        var token = secrets.Unprotect(config.ProtectedToken)
                    ?? throw new RemoteAuthException("the saved token cannot be read on this account; pair again");
        return ConnectAsync(config.Url!, config.Fingerprint!, token, config, ct);
    }

    async Task StreamLoopAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = await _api.OpenWebSocketAsync("api/stream", ct).ConfigureAwait(false);
                while (await WebSocketStream.ReceiveTextAsync(ws, ct).ConfigureAwait(false) is { } text)
                {
                    var m = JsonSerializer.Deserialize<StreamMessage>(text, ApiJson.Options);
                    if (m is null) continue;
                    if (m.Kind == StreamMessage.Hello)
                    {
                        if (!first || !_connected)
                        {
                            await _settingsBackend.RefreshAsync(ct).ConfigureAwait(false);
                            await _discovery.LoadAsync(ct).ConfigureAwait(false);
                        }
                        _connected = true;
                        if (!first) ConnectionChanged?.Invoke(true);
                        first = false;
                        continue;
                    }
                    Dispatch(m);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Reconnect below.
            }
            if (_connected)
            {
                _connected = false;
                ConnectionChanged?.Invoke(false);
            }
            first = false;
            try { await Task.Delay(3000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    void Dispatch(StreamMessage m)
    {
        switch (m.Kind)
        {
            case StreamMessage.AgentAdded when m.Agent is { } a: Feed.Added(a); break;
            case StreamMessage.AgentChanged when m.Agent is { } a: Feed.Changed(a); break;
            case StreamMessage.AgentRemoved when m.Id is { } id: Feed.Removed(id); break;
            case StreamMessage.EventLogged when m.Event is { } e: Feed.Logged(e); break;
            case StreamMessage.Screenshot when m.Id is { } id && m.ShotId is { } shot: Feed.Screenshot(id, shot); break;
            case StreamMessage.Inventory when m.Id is { } id: Feed.Inventory(id); break;
            case StreamMessage.DiscoveryFound when m.Discovered is { } d: _discovery.Found(d.ToAgent()); break;
            case StreamMessage.DiscoveryGone when m.Discovered is { } d: _discovery.Lost(d.ToAgent()); break;
            case StreamMessage.Browsing when m.Flag is { } b: _discovery.SetBrowsing(b); break;
            case StreamMessage.Settings: _ = _settingsBackend.RefreshAsync(CancellationToken.None); break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_streamLoop is not null)
        {
            try { await _streamLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        _api.Dispose();
    }
}

/// <summary>Agent connections through the host's /api/tunnel WebSocket.</summary>
sealed class TunnelTransport(ApiClient api) : ITcpTransport
{
    public async Task<Stream> OpenAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(connectTimeout + TimeSpan.FromSeconds(8));
        try
        {
            var ws = await api.OpenWebSocketAsync($"api/tunnel?host={Uri.EscapeDataString(host)}&port={port}", timeout.Token)
                .ConfigureAwait(false);
            return new WebSocketStream(ws);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Protocol.V9xTransportException($"connect to {host}:{port} through the host timed out");
        }
    }
}

sealed class RemoteSettings(ApiClient api, ClientConfig config, Dictionary<string, string> cache) : ISettingsBackend
{
    static readonly HashSet<string> LocalKeys = [AppSettings.CloseToTrayKey, AppSettings.UpdateCheckOnStartupKey];
    readonly object _gate = new();

    public event Action? Changed;

    public string? Get(string key)
    {
        if (LocalKeys.Contains(key)) return config.LocalSettings.GetValueOrDefault(key);
        lock (_gate) return cache.GetValueOrDefault(key);
    }

    public void Set(string key, string value)
    {
        if (LocalKeys.Contains(key))
        {
            config.LocalSettings[key] = value;
            config.Save();
        }
        else
        {
            lock (_gate) cache[key] = value;
            _ = PutAsync(key, value);
        }
        Changed?.Invoke();
    }

    async Task PutAsync(string key, string value)
    {
        try { await api.SendAsync(HttpMethod.Put, $"api/settings/{Uri.EscapeDataString(key)}", new TextValue(value)).ConfigureAwait(false); }
        catch (Exception) { /* the host keeps its value; the next refresh shows it */ }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var fresh = await api.GetAsync<Dictionary<string, string>>("api/settings", ct).ConfigureAwait(false);
            lock (_gate)
            {
                cache.Clear();
                foreach (var kv in fresh) cache[kv.Key] = kv.Value;
            }
            Changed?.Invoke();
        }
        catch (Exception) { /* keep the cache */ }
    }
}

sealed class RemoteDiscovery(ApiClient api) : IDiscoverySource
{
    readonly Dictionary<string, DiscoveredAgent> _agents = new();
    readonly object _gate = new();

    public IReadOnlyList<DiscoveredAgent> Agents
    {
        get { lock (_gate) return _agents.Values.ToList(); }
    }

    public bool IsBrowsing { get; private set; }
    public event Action<DiscoveredAgent>? Changed;
    public event Action<DiscoveredAgent>? Gone;
    public event Action<bool>? BrowsingChanged;

    public async Task LoadAsync(CancellationToken ct)
    {
        var list = await api.GetAsync<List<DiscoveredDto>>("api/discovery", ct).ConfigureAwait(false);
        foreach (var d in list) Found(d.ToAgent());
    }

    public void Found(DiscoveredAgent a)
    {
        lock (_gate) _agents[a.InstanceName] = a;
        Changed?.Invoke(a);
    }

    public void Lost(DiscoveredAgent a)
    {
        lock (_gate) _agents.Remove(a.InstanceName);
        Gone?.Invoke(a);
    }

    public void SetBrowsing(bool value)
    {
        IsBrowsing = value;
        BrowsingChanged?.Invoke(value);
    }

    public Task BrowseNowAsync() => api.SendAsync(HttpMethod.Post, "api/discovery/browse");
}
