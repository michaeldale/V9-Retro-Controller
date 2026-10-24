using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

/// <summary>The plugin's All Agents table, with an instant filter and bulk actions.</summary>
public sealed partial class AgentsViewModel : ViewModelBase
{
    readonly AppServices _services;
    readonly MainWindowViewModel _main;
    readonly Fleet _fleet;

    public AgentsViewModel(AppServices services, Fleet fleet, MainWindowViewModel main)
    {
        _services = services;
        _main = main;
        _fleet = fleet;
        fleet.Rows.CollectionChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    public ObservableCollection<AgentRowViewModel> Visible { get; } = new();

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial AgentRowViewModel? Selected { get; set; }
    [ObservableProperty] public partial string? Busy { get; set; }

    public IList<AgentRowViewModel> SelectedRows { get; set; } = new List<AgentRowViewModel>();

    partial void OnFilterChanged(string value) => ApplyFilter();

    void ApplyFilter()
    {
        var words = Filter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = _fleet.Rows.Where(r => words.All(w => r.Search.Contains(w))).ToList();
        Visible.Clear();
        foreach (var r in wanted) Visible.Add(r);
    }

    IReadOnlyList<AgentRowViewModel> Targets() =>
        SelectedRows.Count > 0 ? SelectedRows.ToList() : Selected is not null ? [Selected] : [];

    [RelayCommand]
    void Open(AgentRowViewModel? row)
    {
        row ??= Selected;
        if (row is not null) _main.OpenAgent(row.Id);
    }

    [RelayCommand]
    Task Add() => _main.AddAgentCommand.ExecuteAsync(null);

    [RelayCommand]
    async Task PollSelected()
    {
        var targets = Targets();
        if (targets.Count == 0) targets = Visible.ToList();
        Busy = $"Polling {targets.Count}…";
        try
        {
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (row, ct) =>
            {
                var result = await _services.Backend.Agents.PollAsync(row.Id, ct: ct);
                if (result.NeedsInventory) await _services.Backend.Agents.RefreshInventoryAsync(row.Id, ct);
            });
        }
        catch (Exception)
        {
            // Failures are recorded on each agent.
        }
        finally
        {
            Busy = null;
        }
    }

    [RelayCommand]
    async Task ScreenshotSelected()
    {
        var targets = Targets().Where(r => r.Agent.CanScreenshot).ToList();
        Busy = $"Capturing {targets.Count}…";
        try
        {
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (row, ct) =>
            {
                try { await _services.Backend.Agents.TakeScreenshotAsync(row.Id, "manual", ct); }
                catch (V9xException) { }
            });
        }
        finally
        {
            Busy = null;
        }
    }

    [RelayCommand]
    async Task ToggleEnabled(AgentRowViewModel? row)
    {
        foreach (var r in row is not null ? [row] : Targets())
        {
            var a = r.Agent;
            await _services.Backend.Agents.UpdateAsync(a.Id, a.Name, a.Host, a.Port, !a.Enabled, a.Notes, a.VncPort, null, false);
        }
    }

    [RelayCommand]
    async Task Edit(AgentRowViewModel? row)
    {
        row ??= Selected;
        if (row is not null) await AgentEditViewModel.ShowEditAsync(_services, row.Agent, _main);
    }

    [RelayCommand]
    async Task Delete(AgentRowViewModel? row)
    {
        var targets = row is not null ? [row] : Targets();
        if (targets.Count == 0) return;
        var what = targets.Count == 1 ? $"\"{targets[0].Name}\"" : $"{targets.Count} agents";
        if (!await Ui.ConfirmAsync("Delete agents", $"Delete {what} with all screenshots, inventory and history?", "Delete", danger: true))
            return;
        foreach (var r in targets)
        {
            var page = _main.OpenAgents.FirstOrDefault(p => p.AgentId == r.Id);
            if (page is not null) _main.CloseAgentPage(page);
            await _services.Backend.Agents.DeleteAsync(r.Id);
        }
    }
}
