using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using V9Retro.Core;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;

namespace V9Retro.Remote.Server;

/// <summary>The HTTPS API that lets another V9 Retro Controller (a Mac, say) work against this
/// host: REST for the store and agent actions, a WebSocket event stream for live updates, and a
/// WebSocket tunnel that carries agent and VNC connections, so the client never needs to reach
/// the retro machines itself. Every request needs a bearer token from <see cref="TokenStore"/>;
/// the certificate is self-signed and pinned by clients.</summary>
public sealed class ApiServer : IAsyncDisposable
{
    public const int DefaultPort = 9870;
    const string EnabledKey = "server_enabled";
    const string PortKey = "server_port";

    readonly CoreHost _core;
    readonly HostDiscovery _discovery;
    WebApplication? _app;
    X509Certificate2? _cert;

    public ApiServer(CoreHost core, HostDiscovery discovery)
    {
        _core = core;
        _discovery = discovery;
        Tokens = new TokenStore(core.SettingsStore);
    }

    public TokenStore Tokens { get; }
    public bool IsRunning => _app is not null;
    public string? LastError { get; private set; }
    public event Action? StateChanged;

    public bool Enabled
    {
        get => _core.SettingsStore.Get(EnabledKey) == "1";
        set => _core.SettingsStore.Set(EnabledKey, value ? "1" : "0");
    }

    public int Port
    {
        get => int.TryParse(_core.SettingsStore.Get(PortKey), out var p) && p is > 0 and < 65536 ? p : DefaultPort;
        set => _core.SettingsStore.Set(PortKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Settings a client may read and change: not tokens, certificate or server switches.</summary>
    public static bool IsSharedSetting(string key) => !key.StartsWith("server_", StringComparison.Ordinal) && !key.StartsWith("api_", StringComparison.Ordinal);

    X509Certificate2 Certificate => _cert ??= CertificateStore.LoadOrCreate(_core.DataDirectory, _core.SettingsStore, _core.Secrets);

    public string Fingerprint => CertificateStore.Fingerprint(Certificate);

    public IReadOnlyList<string> Urls =>
        CertificateStore.LocalAddresses().Select(a => $"https://{a}:{Port}")
            .Append($"https://{Environment.MachineName}:{Port}").ToList();

    /// <summary>A new token packed with this host's address and fingerprint, for the client to paste.</summary>
    public (ApiToken Entry, string Code) CreatePairing(string clientName, string? url = null)
    {
        var (entry, token) = Tokens.Create(clientName);
        return (entry, new PairingCode(url ?? Urls[0], Fingerprint, token).Encode());
    }

    public async Task StartAsync()
    {
        if (_app is not null) return;
        LastError = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "V9RetroController",
                ContentRootPath = _core.DataDirectory,
            });
            builder.Logging.ClearProviders();
            var cert = Certificate;
            var port = Port;
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenAnyIP(port, l => l.UseHttps(cert));
                k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
            });
            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.Use(Authenticate);
            MapEndpoints(app);
            await app.StartAsync();
            _app = app;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StateChanged?.Invoke();
            throw;
        }
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is not null)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3))) await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        StateChanged?.Invoke();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    async Task Authenticate(HttpContext ctx, RequestDelegate next)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : null;
        var entry = Tokens.Validate(token);
        if (entry is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new ErrorDto("auth", "this token is not valid or was revoked on the host"), ApiJson.Options);
            return;
        }
        AgentService.Actor.Value = $"{entry.Name} (remote)";
        await next(ctx);
    }

    static IResult Json<T>(T value) => Results.Json(value, ApiJson.Options);

    static IResult Error(Exception ex) => ex switch
    {
        V9xGuestException g => Results.Json(new ErrorDto("guest", g.Message, (uint)g.Status, g.NativeError, g.Detail), ApiJson.Options, statusCode: 502),
        V9xUnsupportedException => Results.Json(new ErrorDto("unsupported", ex.Message), ApiJson.Options, statusCode: 409),
        V9xTransportException => Results.Json(new ErrorDto("transport", ex.Message), ApiJson.Options, statusCode: 504),
        V9xProtocolException => Results.Json(new ErrorDto("protocol", ex.Message), ApiJson.Options, statusCode: 502),
        V9xException => Results.Json(new ErrorDto("agent", ex.Message), ApiJson.Options, statusCode: 502),
        ArgumentException => Results.Json(new ErrorDto("argument", ex.Message), ApiJson.Options, statusCode: 400),
        InvalidOperationException => Results.Json(new ErrorDto("invalid", ex.Message), ApiJson.Options, statusCode: 409),
        _ => Results.Json(new ErrorDto("internal", ex.Message), ApiJson.Options, statusCode: 500),
    };

    void MapEndpoints(WebApplication app)
    {
        var a = _core.Agents;
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (OperationCanceledException) when (ctx.HttpContext.RequestAborted.IsCancellationRequested) { return Results.Empty; }
            catch (Exception ex) { return Error(ex); }
        });

        api.MapGet("/hello", () => Json(new HelloDto("V9 Retro Controller", Environment.MachineName,
            typeof(ApiServer).Assembly.GetName().Version?.ToString(3) ?? "", Environment.UserName)));

        api.MapGet("/agents", () => Json(a.All().Select(ApiJson.ForWire)));
        api.MapGet("/agents/{id:long}", (long id) => a.Get(id) is { } x ? Json(ApiJson.ForWire(x)) : Results.NotFound());
        api.MapGet("/agents/find", (string? installId, string host, int port) =>
            a.FindExisting(installId, host, port) is { } x ? Json(ApiJson.ForWire(x)) : Results.NoContent());
        api.MapPost("/agents", (AddAgentRequest r) => Json(ApiJson.ForWire(a.Add(r.Name, r.Host, r.Port, r.Notes, r.InstallId, r.Enabled))));
        api.MapPut("/agents/{id:long}", (long id, UpdateAgentRequest r) => Json(ApiJson.ForWire(
            a.Update(id, r.Name, r.Host, r.Port, r.Enabled, r.Notes, r.VncPort, r.VncPassword, r.ChangePassword))));
        api.MapDelete("/agents/{id:long}", (long id) => { a.Delete(id); return Results.NoContent(); });

        api.MapGet("/events", (int? limit) => Json(a.RecentEvents(limit ?? 20)));
        api.MapGet("/agents/{id:long}/events", (long id, int? limit) => Json(a.Events(id, limit ?? 15)));
        api.MapGet("/agents/{id:long}/inventory", (long id) => Json(a.Inventory(id)));
        api.MapGet("/agents/{id:long}/screenshots", (long id, int? limit) => Json(a.Screenshots(id, limit ?? 10)));
        api.MapGet("/screenshots/{id:long}/thumb", (long id) => a.Thumbnail(id) is { } b ? Results.Bytes(b, "image/png") : Results.NotFound());
        api.MapGet("/screenshots/{id:long}/png", (long id) => a.ScreenshotPng(id) is { } b ? Results.Bytes(b, "image/png") : Results.NotFound());
        api.MapGet("/agents/{id:long}/vnc-password", (long id) => Json(new TextValue(a.Get(id) is { } x ? a.VncPassword(x) : null)));

        api.MapPost("/agents/{id:long}/poll", async (long id, bool? throttle, CancellationToken ct) =>
        {
            var r = await a.PollAsync(id, throttle ?? false, ct);
            return Json(r with { Agent = ApiJson.ForWire(r.Agent) });
        });
        api.MapPost("/agents/{id:long}/inventory", async (long id, CancellationToken ct) => Json(ApiJson.ForWire(await a.RefreshInventoryAsync(id, ct))));
        api.MapPost("/agents/{id:long}/screenshot", async (long id, string? source, CancellationToken ct) =>
            Json(new { id = await a.TakeScreenshotAsync(id, source ?? "manual", ct) }));
        api.MapPost("/agents/{id:long}/power/{action}", async (long id, PowerAction action, CancellationToken ct) =>
            Json(ApiJson.ForWire(await a.PowerAsync(id, action, ct))));
        api.MapPost("/agents/{id:long}/resolve-conflict", (long id) => Json(ApiJson.ForWire(a.ResolveIdentityConflict(id))));
        api.MapPost("/agents/{id:long}/log", (long id, TextValue body) => { a.LogAction(id, body.Value ?? ""); return Results.NoContent(); });

        api.MapGet("/settings", () => Json(_core.SettingsStore.All().Where(kv => IsSharedSetting(kv.Key)).ToDictionary()));
        api.MapPut("/settings/{key}", (string key, TextValue body) =>
        {
            if (!IsSharedSetting(key)) return Results.Json(new ErrorDto("argument", "that setting can only be changed on the host"), ApiJson.Options, statusCode: 403);
            _core.SettingsStore.Set(key, body.Value ?? "");
            return Results.NoContent();
        });

        api.MapGet("/discovery", () => Json(_discovery.Agents.Select(DiscoveredDto.From)));
        api.MapPost("/discovery/browse", async () => { await _discovery.BrowseNowAsync(); return Results.NoContent(); });

        app.Map("/api/stream", StreamAsync);
        app.Map("/api/tunnel", TunnelAsync);
    }

    /// <summary>Pushes change-feed, discovery and settings events to one client until it leaves.</summary>
    async Task StreamAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var channel = Channel.CreateUnbounded<StreamMessage>(new UnboundedChannelOptions { SingleReader = true });
        void Post(StreamMessage m) => channel.Writer.TryWrite(m);

        Action<Agent> added = x => Post(new StreamMessage(StreamMessage.AgentAdded) { Agent = ApiJson.ForWire(x) });
        Action<Agent> changed = x => Post(new StreamMessage(StreamMessage.AgentChanged) { Agent = ApiJson.ForWire(x) });
        Action<long> removed = id => Post(new StreamMessage(StreamMessage.AgentRemoved) { Id = id });
        Action<AgentEvent> logged = e => Post(new StreamMessage(StreamMessage.EventLogged) { Event = e });
        Action<long, long> shot = (id, s) => Post(new StreamMessage(StreamMessage.Screenshot) { Id = id, ShotId = s });
        Action<long> inventory = id => Post(new StreamMessage(StreamMessage.Inventory) { Id = id });
        Action<V9Retro.Discovery.DiscoveredAgent> found = d => Post(new StreamMessage(StreamMessage.DiscoveryFound) { Discovered = DiscoveredDto.From(d) });
        Action<V9Retro.Discovery.DiscoveredAgent> gone = d => Post(new StreamMessage(StreamMessage.DiscoveryGone) { Discovered = DiscoveredDto.From(d) });
        Action<bool> browsing = b => Post(new StreamMessage(StreamMessage.Browsing) { Flag = b });
        Action settings = () => Post(new StreamMessage(StreamMessage.Settings));

        var feed = _core.Feed;
        feed.AgentAdded += added;
        feed.AgentChanged += changed;
        feed.AgentRemoved += removed;
        feed.EventLogged += logged;
        feed.ScreenshotAdded += shot;
        feed.InventoryChanged += inventory;
        _discovery.Changed += found;
        _discovery.Gone += gone;
        _discovery.BrowsingChanged += browsing;
        _core.SettingsStore.Changed += settings;
        try
        {
            // The client never sends; reading notices when it goes away.
            _ = Task.Run(async () =>
            {
                try { while (await WebSocketStream.ReceiveTextAsync(ws, ctx.RequestAborted) is not null) { } }
                catch (Exception) { }
                channel.Writer.TryComplete();
            });
            Post(new StreamMessage(StreamMessage.Hello));
            await foreach (var m in channel.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(m, ApiJson.Options);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ctx.RequestAborted);
            }
        }
        catch (Exception) when (ws.State != WebSocketState.Open || ctx.RequestAborted.IsCancellationRequested)
        {
            // Client left.
        }
        finally
        {
            feed.AgentAdded -= added;
            feed.AgentChanged -= changed;
            feed.AgentRemoved -= removed;
            feed.EventLogged -= logged;
            feed.ScreenshotAdded -= shot;
            feed.InventoryChanged -= inventory;
            _discovery.Changed -= found;
            _discovery.Gone -= gone;
            _discovery.BrowsingChanged -= browsing;
            _core.SettingsStore.Changed -= settings;
        }
    }

    /// <summary>Opens TCP to host:port on the LAN (see <see cref="TunnelPolicy"/>) and relays bytes
    /// both ways over the WebSocket. Connect errors are returned before the upgrade, as JSON.</summary>
    async Task TunnelAsync(HttpContext ctx)
    {
        var host = ctx.Request.Query["host"].ToString();
        if (!ctx.WebSockets.IsWebSocketRequest || host.Length == 0 || !int.TryParse(ctx.Request.Query["port"], out var port)
            || port is < 1 or > 65535)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ctx.RequestAborted);
        }
        catch (SocketException ex)
        {
            await WriteError(ctx, 504, new ErrorDto("transport", $"the host cannot resolve {host}: {ex.Message}"));
            return;
        }
        var target = addresses.FirstOrDefault(TunnelPolicy.IsAllowed);
        if (target is null)
        {
            await WriteError(ctx, 403, new ErrorDto("argument", $"{host} is not a private LAN address; the host only tunnels to its local network"));
            return;
        }

        using var tcp = new TcpClient(target.AddressFamily) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(target, port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            await WriteError(ctx, 504, new ErrorDto("transport",
                ex is OperationCanceledException ? $"connect to {host}:{port} timed out (from the host)" : $"connect to {host}:{port} failed (from the host): {ex.Message}"));
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await using var wsStream = new WebSocketStream(ws);
        var net = tcp.GetStream();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var up = wsStream.CopyToAsync(net, 64 * 1024, stop.Token);
        var down = net.CopyToAsync(wsStream, 64 * 1024, stop.Token);
        try { await Task.WhenAny(up, down); }
        finally
        {
            stop.Cancel();
            tcp.Close();
        }
    }

    /// <summary>WebSocket clients cannot read a refused upgrade's body, so the error also goes in headers.</summary>
    public const string ErrorHeader = "X-V9R-Error", ErrorKindHeader = "X-V9R-Error-Kind";

    static async Task WriteError(HttpContext ctx, int status, ErrorDto error)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers[ErrorKindHeader] = error.Kind;
        ctx.Response.Headers[ErrorHeader] = new string(error.Message.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        await ctx.Response.WriteAsJsonAsync(error, ApiJson.Options);
    }
}
