using V9Retro.Core.Model;
using V9Retro.Protocol;
using V9Retro.Protocol.Imaging;

namespace V9Retro.Core.Services;

/// <summary>How a byte stream to an agent (or its VNC server) is opened: a direct TCP socket on
/// the host, or a tunnel through a remote V9 Retro Controller host.</summary>
public interface ITcpTransport
{
    Task<Stream> OpenAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct);
}

public sealed class DirectTransport : ITcpTransport
{
    public static readonly DirectTransport Instance = new();

    public Task<Stream> OpenAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct) =>
        V9xSession.OpenTcpAsync(host, port, connectTimeout, ct);
}

/// <summary>Opens agent sessions within the agent's connection budget (see <see cref="AgentConnector"/>).</summary>
public interface IAgentConnector
{
    Task<T> UseAsync<T>(string host, int port, Func<V9xSession, Task<T>> body, CancellationToken ct = default, TimeSpan? readTimeout = null);
    Task UseAsync(string host, int port, Func<V9xSession, Task> body, CancellationToken ct = default);
    Task<AgentConnector.SessionLease> OpenLongAsync(string host, int port, CancellationToken ct = default);
    /// <summary>A raw stream to any port on the agent's machine, for the VNC viewer.</summary>
    Task<Stream> OpenRawAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct = default);
}

/// <summary>Everything the UI asks of the store and the agents. Implemented locally by
/// <see cref="AgentService"/> and remotely by the API client, so the same UI runs on the host
/// or on a Mac that connects to it.</summary>
public interface IAgentService
{
    ChangeFeed Feed { get; }
    IAgentConnector Connector { get; }

    Task<IReadOnlyList<Agent>> AllAsync(CancellationToken ct = default);
    Task<Agent?> GetAsync(long id, CancellationToken ct = default);
    Task<Agent?> FindExistingAsync(string? installId, string host, int port, CancellationToken ct = default);
    Task<IReadOnlyList<AgentEvent>> RecentEventsAsync(int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<AgentEvent>> EventsAsync(long agentId, int limit = 15, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryRecord>> InventoryAsync(long agentId, CancellationToken ct = default);
    Task<IReadOnlyList<ScreenshotRecord>> ScreenshotsAsync(long agentId, int limit = 10, CancellationToken ct = default);
    Task<byte[]?> ThumbnailAsync(long screenshotId, CancellationToken ct = default);
    Task<byte[]?> ScreenshotPngAsync(long screenshotId, CancellationToken ct = default);
    Task<string?> VncPasswordAsync(long agentId, CancellationToken ct = default);

    Task<PollResult> PollAsync(long agentId, bool throttle = false, CancellationToken ct = default);
    Task<Agent> RefreshInventoryAsync(long agentId, CancellationToken ct = default);
    /// <summary>Captures and stores a screenshot; returns its id.</summary>
    Task<long> TakeScreenshotAsync(long agentId, string source, CancellationToken ct = default);
    Task<Agent> PowerAsync(long agentId, PowerAction action, CancellationToken ct = default);

    Task<Agent> AddAsync(string name, string host, int port, string? notes = null, string? installId = null, bool enabled = true,
        CancellationToken ct = default);
    Task<Agent> UpdateAsync(long id, string name, string host, int port, bool enabled, string? notes, int vncPort, string? vncPassword,
        bool changePassword, CancellationToken ct = default);
    Task<Agent> ResolveIdentityConflictAsync(long id, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task LogActionAsync(long agentId, string message, CancellationToken ct = default);
}

/// <summary>Key/value settings storage: the local SQLite table, or the remote host's.</summary>
public interface ISettingsBackend
{
    string? Get(string key);
    void Set(string key, string value);
    /// <summary>Raised after any change, including ones made elsewhere (another client).</summary>
    event Action? Changed;
}

/// <summary>Operations that only need a connection, so they run the same over a direct socket or a tunnel.</summary>
public static class AgentOps
{
    /// <summary>HELLO + INFO against an address, for the add form's Test button.</summary>
    public static async Task<ConnectionTest> TestConnectionAsync(IAgentConnector connector, string host, int port, CancellationToken ct = default)
    {
        var started = Environment.TickCount64;
        return await connector.UseAsync(host, port, async s =>
        {
            var info = await s.InfoAsync(ct).ConfigureAwait(false);
            return new ConnectionTest(info, s.Hello, Environment.TickCount64 - started);
        }, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>A screenshot that is not stored (quick-connect agents, the Control tab, window captures).</summary>
    public static async Task<(BgraImage Image, ScreenshotInfo Info)> CaptureAsync(IAgentConnector connector, string host, int port,
        ScreenshotOptions? options = null, CancellationToken ct = default)
    {
        var (info, bmp) = await connector.UseAsync(host, port, s => s.ScreenshotAsync(options ?? new ScreenshotOptions(), ct: ct),
            ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return (BmpDecoder.Decode(bmp), info);
    }
}
