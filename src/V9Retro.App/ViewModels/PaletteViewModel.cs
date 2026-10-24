using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace V9Retro.App.ViewModels;

public sealed record PaletteItem(string Title, string Subtitle, Func<Task> Run);

/// <summary>Ctrl+K: type part of a name or an address to jump to an agent or run an action on it
/// ("reboot retro", "vnc dos", "10.0.0.5:9869").</summary>
public sealed partial class PaletteViewModel : ViewModelBase
{
    static readonly (string Verb, string Label)[] Verbs =
    [
        ("open", "Open"), ("screenshot", "Screenshot"), ("vnc", "VNC"), ("console", "Console"), ("files", "Files"),
        ("reboot", "Reboot"), ("poll", "Poll now"),
    ];

    readonly Fleet _fleet;
    readonly MainWindowViewModel _main;

    public PaletteViewModel(Fleet fleet, MainWindowViewModel main)
    {
        _fleet = fleet;
        _main = main;
    }

    public ObservableCollection<PaletteItem> Results { get; } = new();

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial PaletteItem? Selected { get; set; }

    public void Open()
    {
        Query = "";
        Rebuild();
        IsOpen = true;
    }

    partial void OnQueryChanged(string value) => Rebuild();

    void Rebuild()
    {
        Results.Clear();
        var words = Query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var verb = words.Count > 0 ? Verbs.FirstOrDefault(v => v.Verb.StartsWith(words[0], StringComparison.Ordinal) && words[0].Length >= 2) : default;
        var rest = verb.Verb is not null && words.Count > 1 ? words.Skip(1).ToList() : words;

        if (MainWindowViewModel.TryParseAddress(Query, out var host, out var port) && (host.Contains('.') || Query.Contains(':')))
            Results.Add(new PaletteItem($"Connect to {host}:{port}", "Open without saving", () => { _main.OpenTemporary(host, port); return Task.CompletedTask; }));

        foreach (var row in _fleet.Rows.Where(r => rest.All(w => r.Search.Contains(w))).Take(12))
        {
            var verbs = verb.Verb is not null ? [verb] : Verbs.Take(1);
            foreach (var (v, label) in verbs)
                Results.Add(new PaletteItem($"{label} {row.Name}", $"{row.Address} · {row.StatusText}", () => RunAsync(v, row.Id)));
        }
        if (words.Count == 0 || "add agent".Contains(Query.Trim().ToLowerInvariant()))
            Results.Add(new PaletteItem("Add agent…", "Enter an address by hand", () => _main.AddAgentCommand.ExecuteAsync(null)));
        foreach (var nav in _main.NavItems.Where(n => words.Count == 0 || n.Title.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)))
            Results.Add(new PaletteItem($"Go to {nav.Title}", "", () => { _main.ShowPage(nav.Page); return Task.CompletedTask; }));
        Selected = Results.FirstOrDefault();
    }

    async Task RunAsync(string verb, long id)
    {
        _main.OpenAgent(id);
        if (_main.CurrentPage is not AgentPageViewModel page) return;
        switch (verb)
        {
            case "screenshot": await page.Overview.TakeScreenshotCommand.ExecuteAsync(null); break;
            case "vnc": page.SelectTab(page.Vnc); break;
            case "console": page.SelectTab(page.Console); break;
            case "files": page.SelectTab(page.Files); break;
            case "reboot": await page.RebootCommand.ExecuteAsync(null); break;
            case "poll": await page.RefreshCommand.ExecuteAsync(null); break;
        }
    }

    [RelayCommand]
    async Task Run(PaletteItem? item)
    {
        item ??= Selected;
        IsOpen = false;
        if (item is not null) await item.Run();
    }

    [RelayCommand]
    void Close() => IsOpen = false;

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }
}
