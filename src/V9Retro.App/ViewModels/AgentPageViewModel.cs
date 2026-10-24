using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Data;
using V9Retro.Core.Formatting;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

/// <summary>One tab on an agent page. Tabs load lazily the first time they are shown.</summary>
public abstract partial class AgentTabViewModel(AgentPageViewModel page, string title) : ViewModelBase
{
    public AgentPageViewModel Page { get; } = page;
    public string Title { get; } = title;

    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Busy { get; set; }

    bool _loaded;

    /// <summary>Why the tab cannot work with this agent, or null.</summary>
    public virtual string? Unavailable => null;

    public void Shown()
    {
        if (_loaded) return;
        _loaded = true;
        _ = OnFirstShownAsync();
    }

    protected virtual Task OnFirstShownAsync() => Task.CompletedTask;

    public virtual void AgentChanged() => OnPropertyChanged(nameof(Unavailable));

    /// <summary>Runs an operation with the Busy text shown and any error captured for the tab.</summary>
    protected async Task Run(string busy, Func<Task> body)
    {
        Busy = busy;
        Error = null;
        try { await body(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = Ui.Describe(ex); }
        finally { Busy = null; }
    }

    protected Task<T> UseAsync<T>(Func<V9xSession, Task<T>> body, TimeSpan? timeout = null) => Page.UseAsync(body, timeout);
}

/// <summary>An agent's page: header with status and actions, live polling while visible, and the
/// tool tabs. Works for saved agents and for temporary (quick-connect) ones, which poll INFO
/// directly and keep their state in memory until saved.</summary>
public sealed partial class AgentPageViewModel : ViewModelBase, IDisposable
{
    readonly AppServices _services;
    readonly MainWindowViewModel _main;
    CancellationTokenSource? _pollLoop;

    public AgentPageViewModel(AppServices services, MainWindowViewModel main, Agent agent)
    {
        _services = services;
        _main = main;
        Agent = agent;
        Overview = new OverviewTab(this);
        Inventory = new InventoryTab(this);
        Console = new ConsoleTab(this);
        Files = new FilesTab(this);
        Processes = new ProcessesTab(this);
        Registry = new RegistryTab(this);
        Windows = new WindowsTab(this);
        Control = new ControlTab(this);
        Vnc = new VncTab(this);
        Maintenance = new MaintenanceTab(this);
        Tabs = [Overview, Inventory, Console, Files, Processes, Registry, Windows, Control, Vnc, Maintenance];
        SelectedTab = Overview;

        services.Backend.Feed.AgentChanged += OnFeedAgentChanged;
        services.Backend.Feed.EventLogged += OnEvent;
        if (IsSaved) _ = LoadEventsAsync();
    }

    public AppServices Services => _services;
    public MainWindowViewModel Main => _main;

    [ObservableProperty] public partial Agent Agent { get; set; }
    [ObservableProperty] public partial AgentTabViewModel SelectedTab { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Busy { get; set; }
    [ObservableProperty] public partial bool PollFlash { get; set; }

    public ObservableCollection<EventRow> Activity { get; } = new();

    public OverviewTab Overview { get; }
    public InventoryTab Inventory { get; }
    public ConsoleTab Console { get; }
    public FilesTab Files { get; }
    public ProcessesTab Processes { get; }
    public RegistryTab Registry { get; }
    public WindowsTab Windows { get; }
    public ControlTab Control { get; }
    public VncTab Vnc { get; }
    public MaintenanceTab Maintenance { get; }
    public IReadOnlyList<AgentTabViewModel> Tabs { get; } = [];

    public long AgentId => Agent.Id;
    public bool IsSaved => Agent.IsSaved;
    public string Name => Agent.Name;
    public string Status => Agent.Status;
    public string StatusText => Agent.IdentityConflict is not null ? "Identity conflict" : V9xFormat.StatusLabel(Agent.Status);
    public string Address => Agent.Address;
    public string InstallId => Agent.InstallId ?? "no install ID";
    public string LastSeen => "seen " + V9xFormat.Ago(Agent.LastSeen);
    public string RailTitle => IsSaved ? Agent.Name : Agent.Name + " (unsaved)";

    public bool IsOnline => Agent.IsOnline;
    public bool CanPower => Agent.IsOnline && Agent.Has(Capabilities.PowerControl);
    public bool CanReset => Agent.IsOnline && Agent.Has(Capabilities.HardReset);

    /// <summary>The banner under the header: offline with the last error, rebooting, shut down, conflict.</summary>
    public string? Banner => Agent.IdentityConflict is { } other
        ? $"A different machine (install {other}) now answers at {Agent.Address}. Resolve to track it as this agent, or edit the address."
        : Agent.Status switch
        {
            AgentStatus.Offline => "Offline: " + (Agent.LastError ?? "no answer"),
            AgentStatus.Rebooting => "Rebooting: waiting for the agent to come back with a new boot counter and our token.",
            AgentStatus.Shutdown => "Shut down from here. It will show online again when it answers.",
            _ => null,
        };

    public bool HasConflict => Agent.IdentityConflict is not null;

    partial void OnAgentChanged(Agent value)
    {
        OnPropertyChanged((string?)null);
        foreach (var t in Tabs) t.AgentChanged();
    }

    partial void OnSelectedTabChanged(AgentTabViewModel value) => value?.Shown();

    public void SelectTab(AgentTabViewModel tab) => SelectedTab = tab;

    void OnFeedAgentChanged(Agent a)
    {
        if (a.Id == Agent.Id && IsSaved) Ui.Post(() => Agent = a);
    }

    void OnEvent(AgentEvent e)
    {
        if (e.AgentId != Agent.Id || !IsSaved) return;
        Ui.Post(() =>
        {
            Activity.Insert(0, EventRow.From(e, Agent.Name));
            while (Activity.Count > 15) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    async Task LoadEventsAsync()
    {
        IReadOnlyList<AgentEvent> events;
        try { events = await _services.Backend.Agents.EventsAsync(Agent.Id); }
        catch (Exception) { return; }
        Activity.Clear();
        foreach (var e in events) Activity.Add(EventRow.From(e, Agent.Name));
    }

    public Task<T> UseAsync<T>(Func<V9xSession, Task<T>> body, TimeSpan? timeout = null) =>
        _services.Backend.Agents.Connector.UseAsync(Agent.Host, Agent.Port, body, default, timeout);

    // ---- live polling ----------------------------------------------------------------

    public void Activate()
    {
        SelectedTab?.Shown();
        if (_pollLoop is not null) return;
        _pollLoop = new CancellationTokenSource();
        _ = PollLoopAsync(_pollLoop.Token);
    }

    public void Deactivate()
    {
        _pollLoop?.Cancel();
        _pollLoop = null;
        Overview.StopLive();
    }

    async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(throttle: true);
            var delay = Agent.Status switch
            {
                AgentStatus.Rebooting => 3000,
                AgentStatus.Online => 5000,
                _ => 10000,
            };
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task PollOnceAsync(bool throttle)
    {
        try
        {
            if (IsSaved)
            {
                var result = await _services.Backend.Agents.PollAsync(Agent.Id, throttle);
                Agent = result.Agent;
                if (result.NeedsInventory)
                {
                    Agent = await _services.Backend.Agents.RefreshInventoryAsync(Agent.Id);
                    await Inventory.ReloadAsync();
                }
            }
            else
            {
                Agent = await PollTemporaryAsync(Agent);
            }
            PollFlash = !PollFlash;
        }
        catch (InvalidOperationException)
        {
            // Deleted while polling.
        }
        catch (Exception ex)
        {
            Error = Ui.Describe(ex);
        }
    }

    async Task<Agent> PollTemporaryAsync(Agent before)
    {
        try
        {
            var info = await _services.Backend.Agents.Connector.UseAsync(before.Host, before.Port, s => s.InfoAsync());
            return StatusRules.Success(before, info, Clock.Now()).Agent;
        }
        catch (V9xException ex)
        {
            return StatusRules.Failure(before, ex.Message, Clock.Now(), 1).Agent;
        }
    }

    [RelayCommand]
    async Task Refresh()
    {
        Busy = "Polling…";
        try
        {
            await PollOnceAsync(throttle: false);
            if (IsSaved && Agent.IsOnline && Agent.Has(Capabilities.Inventory))
            {
                Agent = await _services.Backend.Agents.RefreshInventoryAsync(Agent.Id);
                await Inventory.ReloadAsync();
            }
        }
        catch (Exception ex)
        {
            Error = Ui.Describe(ex);
        }
        finally
        {
            Busy = null;
        }
    }

    // ---- power -----------------------------------------------------------------------

    async Task PowerAsync(PowerAction action, string title, string message)
    {
        if (!await Ui.ConfirmAsync(title, message, title, danger: true)) return;
        Busy = title + "…";
        Error = null;
        try
        {
            if (IsSaved)
            {
                Agent = await _services.Backend.Agents.PowerAsync(Agent.Id, action);
            }
            else
            {
                var token = V9xSession.NewPowerToken();
                await UseAsync(s => action switch
                {
                    PowerAction.Reboot => s.RebootAsync(token),
                    PowerAction.Shutdown => s.ShutdownAsync(token),
                    _ => s.ResetAsync(token),
                });
                var a = Agent.Clone();
                a.Status = action == PowerAction.Shutdown ? AgentStatus.Shutdown : AgentStatus.Rebooting;
                a.PowerToken = token;
                a.PowerAction = action.ToString().ToLowerInvariant();
                a.PowerRequestedAt = Clock.Now();
                Agent = a;
            }
            Overview.StopLive();
        }
        catch (V9xGuestException ex) when (ex.Status == V9xStatus.Busy)
        {
            Error = "The agent is busy: a program started through it is still running. Cancel it first.";
        }
        catch (Exception ex)
        {
            Error = Ui.Describe(ex);
        }
        finally
        {
            Busy = null;
        }
    }

    [RelayCommand]
    Task Reboot() => PowerAsync(PowerAction.Reboot, "Reboot", $"Reboot {Agent.Name}? Unsaved work on that machine is lost.");

    [RelayCommand]
    Task Shutdown() => PowerAsync(PowerAction.Shutdown, "Shut down", $"Shut down {Agent.Name}? You will need to power it on again by hand.");

    [RelayCommand]
    Task Reset() => PowerAsync(PowerAction.Reset, "Hard reset",
        $"Hard-reset {Agent.Name}? The agent flushes the disks, then resets the machine without Windows' restart (for when a normal reboot hangs).");

    // ---- saving and editing ----------------------------------------------------------

    [RelayCommand]
    async Task Edit()
    {
        if (!IsSaved) return;
        var saved = await AgentEditViewModel.ShowEditAsync(_services, Agent, _main);
        if (saved is not null) Agent = saved;
    }

    [RelayCommand]
    async Task Save()
    {
        if (IsSaved) return;
        var saved = await AgentEditViewModel.ShowAddAsync(_services, Agent.ComputerName ?? Agent.Name, Agent.Host, Agent.Port, Agent.InstallId);
        if (saved is null) return;
        Agent = await _services.Backend.Agents.GetAsync(saved.Id) ?? saved;
        await LoadEventsAsync();
        _main.PageSaved(this);
    }

    [RelayCommand]
    async Task ResolveConflict()
    {
        try
        {
            Agent = await _services.Backend.Agents.ResolveIdentityConflictAsync(Agent.Id);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    void Close() => _main.CloseAgentPage(this);

    public void Dispose()
    {
        _services.Backend.Feed.AgentChanged -= OnFeedAgentChanged;
        _services.Backend.Feed.EventLogged -= OnEvent;
        foreach (var t in Tabs) (t as IDisposable)?.Dispose();
    }
}
