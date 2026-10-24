using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Model;
using V9Retro.Protocol.Wire;

namespace V9Retro.App.ViewModels;

public sealed record NavItem(string Title, string Glyph, object Page);

public sealed record Toast(string Title, string Message, string Status);

/// <summary>The shell: left rail (fixed pages plus open agent pages), quick connect, the command
/// palette and toast notifications.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    readonly AppServices _services;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Fleet = new Fleet(services.Backend);
        Dashboard = new DashboardViewModel(services, Fleet, this);
        Agents = new AgentsViewModel(services, Fleet, this);
        Discover = new DiscoverViewModel(services, Fleet, this);
        Updates = new UpdatesViewModel(services);
        Settings = new SettingsViewModel(services, Updates);
        Updates.Found += u => ToastRaised?.Invoke(new Toast("Update available", $"V9 Retro Controller {u.Version} is ready to install (Settings → Updates).", "online"));
        Palette = new PaletteViewModel(Fleet, this);
        NavItems =
        [
            new("Dashboard", "▦", Dashboard),
            new("Agents", "☰", Agents),
            new("Discover", "◎", Discover),
            new("Settings", "⚙", Settings),
        ];
        SelectedNav = NavItems[0];

        services.Backend.ConnectionChanged += up => Ui.Post(() => ConnectionLost = !up);
        services.Backend.Feed.EventLogged += e =>
        {
            if (e.Type is not (AgentEventType.Online or AgentEventType.Offline or AgentEventType.Rebooted
                or AgentEventType.RebootTimeout or AgentEventType.IdentityConflict or AgentEventType.AddressChanged)) return;
            Ui.Post(() =>
            {
                var name = Fleet.Find(e.AgentId)?.Name ?? "Agent";
                var status = e.Type switch
                {
                    AgentEventType.Online or AgentEventType.Rebooted => "online",
                    AgentEventType.Offline => "offline",
                    _ => "rebooting",
                };
                ToastRaised?.Invoke(new Toast(name, e.Message, status));
            });
        };
    }

    public Fleet Fleet { get; }
    public DashboardViewModel Dashboard { get; }
    public AgentsViewModel Agents { get; }
    public DiscoverViewModel Discover { get; }
    public SettingsViewModel Settings { get; }
    public UpdatesViewModel Updates { get; }
    public PaletteViewModel Palette { get; }

    public IReadOnlyList<NavItem> NavItems { get; }
    public ObservableCollection<AgentPageViewModel> OpenAgents { get; } = new();

    /// <summary>The view raises toasts with its notification manager.</summary>
    public event Action<Toast>? ToastRaised;

    [ObservableProperty] public partial object? CurrentPage { get; set; }
    [ObservableProperty] public partial NavItem? SelectedNav { get; set; }
    [ObservableProperty] public partial AgentPageViewModel? SelectedAgentPage { get; set; }
    [ObservableProperty] public partial string QuickConnectText { get; set; } = "";
    [ObservableProperty] public partial string? QuickConnectError { get; set; }
    [ObservableProperty] public partial bool ConnectionLost { get; set; }

    public string? HostLabel => _services.Backend.IsRemote ? "Host: " + _services.Backend.Location : null;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        SelectedAgentPage = null;
        Show(value.Page);
    }

    partial void OnSelectedAgentPageChanged(AgentPageViewModel? value)
    {
        if (value is null) return;
        SelectedNav = null;
        Show(value);
    }

    void Show(object page)
    {
        if (CurrentPage is AgentPageViewModel old && !ReferenceEquals(old, page)) old.Deactivate();
        CurrentPage = page;
        if (page is AgentPageViewModel agentPage) agentPage.Activate();
        if (page is DashboardViewModel d) d.Refresh();
    }

    public void ShowPage(object page)
    {
        var nav = NavItems.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (nav is not null) SelectedNav = nav;
    }

    /// <summary>Opens (or focuses) a saved agent's page.</summary>
    public void OpenAgent(long id)
    {
        var page = OpenAgents.FirstOrDefault(p => p.AgentId == id);
        if (page is null)
        {
            if (Fleet.Find(id)?.Agent is not { } agent)
            {
                _ = OpenLoadedAsync(id);
                return;
            }
            page = new AgentPageViewModel(_services, this, agent);
            OpenAgents.Add(page);
        }
        SelectedAgentPage = page;
    }

    /// <summary>An agent the fleet has not loaded yet (just added elsewhere): fetch it, then open.</summary>
    async Task OpenLoadedAsync(long id)
    {
        try
        {
            if (await _services.Backend.Agents.GetAsync(id) is not { } agent) return;
            if (OpenAgents.FirstOrDefault(p => p.AgentId == id) is not { } page)
            {
                page = new AgentPageViewModel(_services, this, agent);
                OpenAgents.Add(page);
            }
            SelectedAgentPage = page;
        }
        catch (Exception)
        {
            // Unreachable host: nothing to open.
        }
    }

    /// <summary>Opens a page for an address without saving it ("control directly").</summary>
    public void OpenTemporary(string host, int port, string? name = null, string? installId = null)
    {
        var existing = Fleet.Rows.FirstOrDefault(r =>
            (installId is { Length: > 0 } && r.Agent.InstallId == installId)
            || (string.Equals(r.Agent.Host, host, StringComparison.OrdinalIgnoreCase) && r.Agent.Port == port))?.Agent;
        if (existing is not null)
        {
            OpenAgent(existing.Id);
            return;
        }
        var page = OpenAgents.FirstOrDefault(p => !p.IsSaved && p.Agent.Host == host && p.Agent.Port == port);
        if (page is null)
        {
            page = new AgentPageViewModel(_services, this,
                new Agent { Name = name ?? host, Host = host, Port = port, InstallId = installId });
            OpenAgents.Add(page);
        }
        SelectedAgentPage = page;
    }

    public void CloseAgentPage(AgentPageViewModel page)
    {
        page.Deactivate();
        page.Dispose();
        var index = OpenAgents.IndexOf(page);
        OpenAgents.Remove(page);
        if (ReferenceEquals(CurrentPage, page))
        {
            if (OpenAgents.Count > 0) SelectedAgentPage = OpenAgents[Math.Min(index, OpenAgents.Count - 1)];
            else SelectedNav = NavItems[0];
        }
    }

    /// <summary>A temporary page was saved: it now tracks the stored agent.</summary>
    public void PageSaved(AgentPageViewModel page) => OnPropertyChanged(nameof(OpenAgents));

    [RelayCommand]
    void QuickConnect()
    {
        QuickConnectError = null;
        if (!TryParseAddress(QuickConnectText, out var host, out var port))
        {
            QuickConnectError = "Enter host or host:port";
            return;
        }
        OpenTemporary(host, port);
        QuickConnectText = "";
    }

    public static bool TryParseAddress(string? text, out string host, out int port)
    {
        host = "";
        port = FrameHeader.DefaultPort;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            text = text[..colon];
        }
        if (text.Length == 0 || text.Contains(' ')) return false;
        host = text;
        return true;
    }

    [RelayCommand]
    async Task AddAgent()
    {
        var saved = await AgentEditViewModel.ShowAddAsync(_services);
        if (saved is not null) OpenAgent(saved.Id);
    }

    [RelayCommand]
    void OpenPalette() => Palette.Open();

    [RelayCommand]
    void ShowUpdates() => ShowPage(Settings);

    [RelayCommand]
    void CloseAgent(AgentPageViewModel? page)
    {
        if (page is not null) CloseAgentPage(page);
    }
}
