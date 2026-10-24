using V9Retro.Core;
using V9Retro.Core.Services;
using V9Retro.Discovery;
using V9Retro.Remote.Server;

namespace V9Retro.Remote;

/// <summary>mDNS results as the UI sees them: the local browser, or the host's through the API.</summary>
public interface IDiscoverySource
{
    IReadOnlyList<DiscoveredAgent> Agents { get; }
    bool IsBrowsing { get; }
    /// <summary>Raised on a background thread for every new or changed agent.</summary>
    event Action<DiscoveredAgent>? Changed;
    event Action<DiscoveredAgent>? Gone;
    event Action<bool>? BrowsingChanged;
    Task BrowseNowAsync();
}

/// <summary>What the UI runs against: this computer's database and poller, or a remote host.</summary>
public interface IControllerBackend : IAsyncDisposable
{
    IAgentService Agents { get; }
    AppSettings Settings { get; }
    MaintenanceService Maintenance { get; }
    ChangeFeed Feed { get; }
    IDiscoverySource Discovery { get; }
    bool IsRemote { get; }
    /// <summary>"This computer", or the host's name and address.</summary>
    string Location { get; }
    /// <summary>The local data folder; null when remote.</summary>
    string? DataDirectory { get; }
    /// <summary>The API server when this computer hosts; null when remote.</summary>
    ApiServer? Server { get; }
    /// <summary>Remote only: the event stream dropped (false) or came back (true). After coming
    /// back, listeners should reload what they show.</summary>
    event Action<bool>? ConnectionChanged;
}

/// <summary>This computer: the SQLite store, the background sweep, local discovery, and the API
/// server when remote access is turned on.</summary>
public sealed class LocalBackend : IControllerBackend
{
    readonly HostDiscovery _discovery;

    public LocalBackend(CoreHost core)
    {
        Core = core;
        _discovery = new HostDiscovery(core);
        Server = new ApiServer(core, _discovery);
    }

    public CoreHost Core { get; }
    public IAgentService Agents => Core.Agents;
    public AppSettings Settings => Core.Settings;
    public MaintenanceService Maintenance => Core.Maintenance;
    public ChangeFeed Feed => Core.Feed;
    public IDiscoverySource Discovery => _discovery;
    public bool IsRemote => false;
    public string Location => "This computer";
    public string? DataDirectory => Core.DataDirectory;
    public ApiServer? Server { get; }

    public event Action<bool>? ConnectionChanged { add { } remove { } }

    /// <summary>Starts the sweep, discovery and (if enabled) the API server.</summary>
    public async Task StartAsync()
    {
        Core.Scheduler.Start();
        _discovery.Start();
        if (Server!.Enabled)
        {
            try { await Server.StartAsync(); }
            catch (Exception) { /* reported through Server.LastError */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Server is not null) await Server.DisposeAsync();
        _discovery.Dispose();
        await Core.DisposeAsync();
    }
}

/// <summary>Runs the mDNS browser for the host: passive listening plus an active browse every
/// minute. Every install seen is matched to saved agents by UUID, so a machine whose DHCP address
/// changed keeps working without editing it.</summary>
public sealed class HostDiscovery : IDiscoverySource, IDisposable
{
    readonly CoreHost _core;
    readonly MdnsBrowser _browser = new();
    readonly CancellationTokenSource _stop = new();
    Task? _loop;

    public HostDiscovery(CoreHost core)
    {
        _core = core;
        _browser.AgentFound += (_, e) => Seen(e.Agent);
        _browser.AgentUpdated += (_, e) => Seen(e.Agent);
        _browser.AgentGone += (_, e) => Gone?.Invoke(e.Agent);
        core.Settings.Changed += () =>
        {
            if (core.Settings.DiscoveryAutoBrowse) Start();
        };
    }

    public event Action<DiscoveredAgent>? Changed;
    public event Action<DiscoveredAgent>? Gone;
    public event Action<bool>? BrowsingChanged;

    public IReadOnlyList<DiscoveredAgent> Agents => _browser.Agents;
    public bool IsBrowsing { get; private set; }

    void Seen(DiscoveredAgent agent)
    {
        if (agent.InstallId is { Length: > 0 } id)
        {
            try { _core.Agents.ObserveDiscovered(id, agent.Host.ToString(), agent.Port); }
            catch (Exception) { /* a database hiccup must not stop discovery */ }
        }
        Changed?.Invoke(agent);
    }

    public void Start()
    {
        if (!_core.Settings.DiscoveryAutoBrowse) return;
        if (!_browser.IsListening) _browser.StartListening();
        _loop ??= Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await BrowseNowAsync().ConfigureAwait(false);
                try { await Task.Delay(TimeSpan.FromSeconds(60), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    public async Task BrowseNowAsync()
    {
        if (IsBrowsing) return;
        IsBrowsing = true;
        BrowsingChanged?.Invoke(true);
        try
        {
            await _browser.BrowseAsync(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* no usable interface; the next round retries */ }
        finally
        {
            IsBrowsing = false;
            BrowsingChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _browser.Dispose();
    }
}
