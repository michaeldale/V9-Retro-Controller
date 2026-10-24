using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Formatting;
using V9Retro.Core.Model;

namespace V9Retro.App.ViewModels;

public sealed record EventRow(long AgentId, string Agent, string When, string Label, string Message, string Type)
{
    public static EventRow From(AgentEvent e, string agentName) =>
        new(e.AgentId, agentName, V9xFormat.Ago(e.DateAdded), AgentEventType.Label(e.Type), e.Message, e.Type);
}

/// <summary>The plugin's dashboard: four count tiles, a card per agent, recent activity.</summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    readonly AppServices _services;
    readonly MainWindowViewModel _main;

    public DashboardViewModel(AppServices services, Fleet fleet, MainWindowViewModel main)
    {
        _services = services;
        _main = main;
        Fleet = fleet;
        fleet.Changed += Recount;
        services.Backend.Feed.EventLogged += e => Ui.Post(() =>
        {
            Activity.Insert(0, EventRow.From(e, Fleet.Find(e.AgentId)?.Name ?? "?"));
            while (Activity.Count > 20) Activity.RemoveAt(Activity.Count - 1);
        });
        _ = RefreshAsync();
    }

    public Fleet Fleet { get; }
    public ObservableCollection<EventRow> Activity { get; } = new();

    [ObservableProperty] public partial int TotalCount { get; set; }
    [ObservableProperty] public partial int OnlineCount { get; set; }
    [ObservableProperty] public partial int OfflineCount { get; set; }
    [ObservableProperty] public partial int OtherCount { get; set; }

    public bool IsEmpty => TotalCount == 0;

    void Recount()
    {
        TotalCount = Fleet.Rows.Count;
        OnlineCount = Fleet.Rows.Count(r => r.Status == AgentStatus.Online);
        OfflineCount = Fleet.Rows.Count(r => r.Status == AgentStatus.Offline);
        OtherCount = TotalCount - OnlineCount - OfflineCount;
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Refresh() => _ = RefreshAsync();

    async Task RefreshAsync()
    {
        Recount();
        IReadOnlyList<AgentEvent> events;
        try { events = await _services.Backend.Agents.RecentEventsAsync(20); }
        catch (Exception) { return; }
        Activity.Clear();
        foreach (var e in events) Activity.Add(EventRow.From(e, Fleet.Find(e.AgentId)?.Name ?? "?"));
    }

    [RelayCommand]
    void Open(AgentRowViewModel? row)
    {
        if (row is not null) _main.OpenAgent(row.Id);
    }

    [RelayCommand]
    void OpenEvent(EventRow? row)
    {
        if (row is not null) _main.OpenAgent(row.AgentId);
    }

    [RelayCommand]
    Task Add() => _main.AddAgentCommand.ExecuteAsync(null);

    [RelayCommand]
    void GoDiscover() => _main.ShowPage(_main.Discover);
}
