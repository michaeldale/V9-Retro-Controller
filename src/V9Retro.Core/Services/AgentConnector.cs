using System.Collections.Concurrent;
using V9Retro.Protocol;

namespace V9Retro.Core.Services;

/// <summary>Opens sessions to agents while respecting their four connection slots: at most
/// <see cref="ShortSlots"/> short operations per address at once (polls, screenshots, file ops),
/// plus <see cref="LongSlots"/> long-lived sessions (an exec console, a file browser). That leaves
/// room for v9xctl or the MCP server on the same machine. The transport decides whether the bytes
/// go straight to the agent or through a remote host.</summary>
public sealed class AgentConnector(ITcpTransport? transport = null) : IAgentConnector
{
    public const int ShortSlots = 2;
    public const int LongSlots = 1;

    sealed class Gate
    {
        public readonly SemaphoreSlim Short = new(ShortSlots, ShortSlots);
        public readonly SemaphoreSlim Long = new(LongSlots, LongSlots);
    }

    readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);
    readonly ITcpTransport _transport = transport ?? DirectTransport.Instance;

    public V9xSessionOptions Options { get; init; } = new() { ClientLabel = "v9retro" };

    Gate GateFor(string host, int port) => _gates.GetOrAdd($"{host}:{port}", _ => new Gate());

    async Task<V9xSession> ConnectAsync(string host, int port, V9xSessionOptions options, CancellationToken ct)
    {
        var stream = await _transport.OpenAsync(host, port, options.ConnectTimeout, ct).ConfigureAwait(false);
        return await V9xSession.ConnectAsync(stream, host, port, options, ct).ConfigureAwait(false);
    }

    /// <summary>Connects, runs <paramref name="body"/>, and always disconnects.</summary>
    public async Task<T> UseAsync<T>(string host, int port, Func<V9xSession, Task<T>> body, CancellationToken ct = default,
        TimeSpan? readTimeout = null)
    {
        var gate = GateFor(host, port).Short;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var options = readTimeout is { } t ? Options with { ReadTimeout = t } : Options;
            await using var session = await ConnectAsync(host, port, options, ct).ConfigureAwait(false);
            return await body(session).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task UseAsync(string host, int port, Func<V9xSession, Task> body, CancellationToken ct = default) =>
        UseAsync<bool>(host, port, async s => { await body(s).ConfigureAwait(false); return true; }, ct);

    /// <summary>A session the caller keeps (console, file browser). Dispose the lease to free the slot.
    /// Throws <see cref="V9xException"/> when this app already holds its long slot for the address.</summary>
    public async Task<SessionLease> OpenLongAsync(string host, int port, CancellationToken ct = default)
    {
        var gate = GateFor(host, port).Long;
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new V9xException("another long-running session to this agent is already open");
        try
        {
            var session = await ConnectAsync(host, port, Options, ct).ConfigureAwait(false);
            return new SessionLease(session, gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public Task<Stream> OpenRawAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct = default) =>
        _transport.OpenAsync(host, port, connectTimeout, ct);

    public sealed class SessionLease : IAsyncDisposable
    {
        readonly SemaphoreSlim _gate;
        int _released;

        internal SessionLease(V9xSession session, SemaphoreSlim gate)
        {
            Session = session;
            _gate = gate;
        }

        public V9xSession Session { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            await Session.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }
}
