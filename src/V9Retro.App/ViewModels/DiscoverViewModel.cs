using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Discovery;

namespace V9Retro.App.ViewModels;

public sealed partial class DiscoveredRow(DiscoveredAgent agent) : ViewModelBase
{
    public DiscoveredAgent Agent { get; private set; } = agent;

    [ObservableProperty] public partial long? KnownId { get; set; }
    [ObservableProperty] public partial string? KnownName { get; set; }
    [ObservableProperty] public partial bool Gone { get; set; }

    public string Name => Agent.Name;
    public string Address => $"{Agent.Host}:{Agent.Port}";
    public string Version => Agent.Version ?? "";
    public string Os => Agent.IsDos ? "DOS" : Agent.OsVersion is { } v ? "Windows " + v : "";
    public string InstallId => Agent.InstallId ?? "";
    public bool IsKnown => KnownId is not null;
    public string State => Gone ? "Gone" : IsKnown ? $"Saved as {KnownName}" : "New";

    partial void OnKnownIdChanged(long? value)
    {
        OnPropertyChanged(nameof(IsKnown));
        OnPropertyChanged(nameof(State));
    }

    partial void OnGoneChanged(bool value) => OnPropertyChanged(nameof(State));

    public void Update(DiscoveredAgent a)
    {
        Agent = a;
        Gone = false;
        OnPropertyChanged((string?)null);
    }
}

/// <summary>Live mDNS results. Each row can be added (pre-filled with name, address and UUID),
/// opened directly without saving, or jumped to when already saved.</summary>
public sealed partial class DiscoverViewModel : ViewModelBase
{
    readonly AppServices _services;
    readonly MainWindowViewModel _main;
    readonly Fleet _fleet;

    public DiscoverViewModel(AppServices services, Fleet fleet, MainWindowViewModel main)
    {
        _services = services;
        _main = main;
        _fleet = fleet;
        foreach (var a in services.Backend.Discovery.Agents) Upsert(a);
        services.Backend.Discovery.Changed += a => Ui.Post(() => Upsert(a));
        services.Backend.Discovery.Gone += a => Ui.Post(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Agent.InstanceName == a.InstanceName);
            if (row is not null) row.Gone = true;
        });
        services.Backend.Discovery.BrowsingChanged += b => Ui.Post(() => IsBrowsing = b);
        fleet.Changed += MatchKnown;
        Status = services.Backend.Settings.DiscoveryAutoBrowse ? "Listening for _v9xremote._tcp agents" : "Automatic discovery is off (Settings)";
    }

    public ObservableCollection<DiscoveredRow> Rows { get; } = new();

    [ObservableProperty] public partial bool IsBrowsing { get; set; }
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial DiscoveredRow? Selected { get; set; }

    void Upsert(DiscoveredAgent a)
    {
        var row = Rows.FirstOrDefault(r => r.Agent.InstanceName == a.InstanceName);
        if (row is null) Rows.Add(row = new DiscoveredRow(a));
        else row.Update(a);
        Match(row);
    }

    void Match(DiscoveredRow row)
    {
        var host = row.Agent.Host.ToString();
        var known = _fleet.Rows.FirstOrDefault(f =>
            (row.Agent.InstallId is { Length: > 0 } id && f.Agent.InstallId == id)
            || (string.Equals(f.Agent.Host, host, StringComparison.OrdinalIgnoreCase) && f.Agent.Port == row.Agent.Port));
        row.KnownId = known?.Id;
        row.KnownName = known?.Name;
    }

    void MatchKnown()
    {
        foreach (var row in Rows) Match(row);
    }

    [RelayCommand]
    async Task Rescan()
    {
        Status = "Browsing…";
        await _services.Backend.Discovery.BrowseNowAsync();
        Status = $"{Rows.Count(r => !r.Gone)} agent(s) found. Machines need [agent] discovery=1 (SETUP.EXE ticks it).";
    }

    [RelayCommand]
    async Task Add(DiscoveredRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (row.KnownId is { } id)
        {
            _main.OpenAgent(id);
            return;
        }
        var saved = await AgentEditViewModel.ShowAddAsync(_services, row.Agent.Name, row.Agent.Host.ToString(), row.Agent.Port,
            row.Agent.InstallId);
        if (saved is not null) _main.OpenAgent(saved.Id);
    }

    [RelayCommand]
    async Task AddAll()
    {
        foreach (var row in Rows.Where(r => !r.IsKnown && !r.Gone).ToList())
        {
            var agent = await _services.Backend.Agents.AddAsync(row.Agent.Name, row.Agent.Host.ToString(), row.Agent.Port, installId: row.Agent.InstallId);
            _ = _services.Backend.Agents.PollAsync(agent.Id);
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    void Connect(DiscoveredRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (row.KnownId is { } id) _main.OpenAgent(id);
        else _main.OpenTemporary(row.Agent.Host.ToString(), row.Agent.Port, row.Agent.Name, row.Agent.InstallId);
    }
}
