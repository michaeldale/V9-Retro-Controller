using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Services;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

public sealed record ProcessRow(ProcessEntry Entry)
{
    public string Pid => $"{Entry.Pid:X8}";
    public string Parent => $"{Entry.ParentPid:X8}";
    public string Name => Entry.Name;
    public string Path => Entry.Image;
    public uint Threads => Entry.Threads;
    public string Kind => Entry.IsAgent ? "agent" : Entry.IsAgentJob ? $"exec #{Entry.ExecRequestId}" : "";
}

/// <summary>PROCESS_LIST / PROCESS_KILL (agent 0.13.0+). Windows 9x pids are opaque, shown in hex.</summary>
public sealed partial class ProcessesTab(AgentPageViewModel page) : AgentTabViewModel(page, "Processes")
{
    public ObservableCollection<ProcessRow> Rows { get; } = new();

    [ObservableProperty] public partial ProcessRow? Selected { get; set; }

    public override string? Unavailable => Page.Agent.Capabilities != 0 && !Page.Agent.Has(Capabilities.Process)
        ? "Process list needs agent 0.13.0 or later (and toolhelp, so not NT 4)."
        : null;

    protected override Task OnFirstShownAsync() => Unavailable is null ? Refresh() : Task.CompletedTask;

    [RelayCommand]
    Task Refresh() => Run("Listing…", async () =>
    {
        var list = await UseAsync(s => s.ProcessListAsync());
        Rows.Clear();
        foreach (var p in list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) Rows.Add(new ProcessRow(p));
    });

    [RelayCommand]
    async Task Kill(ProcessRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (!await Ui.ConfirmAsync("End process", $"End {row.Name} (pid {row.Pid}) on {Page.Name}?", "End process", danger: true)) return;
        await Run("Ending…", async () =>
        {
            await UseAsync(s => s.ProcessKillAsync(row.Entry.Pid));
            if (Page.IsSaved) await Page.Services.Backend.Agents.LogActionAsync(Page.AgentId, $"Ended process {row.Name}");
            await Refresh();
        });
    }
}

public sealed record RegistryValueRow(RegistryValue Value)
{
    public string Name => Value.Name.Length == 0 ? "(Default)" : Value.Name;
    public string Type => Value.Type switch
    {
        RegistryValueType.String => "REG_SZ",
        RegistryValueType.ExpandString => "REG_EXPAND_SZ",
        RegistryValueType.Binary => "REG_BINARY",
        RegistryValueType.DWord => "REG_DWORD",
        RegistryValueType.MultiString => "REG_MULTI_SZ",
        _ => "type " + (uint)Value.Type,
    };
    public string Data => Value.DisplayData;
}

public sealed record HiveOption(RegistryHive Hive, string Name);

/// <summary>Read-only registry browser over REG_QUERY (agent 0.13.0+).</summary>
public sealed partial class RegistryTab : AgentTabViewModel
{
    public RegistryTab(AgentPageViewModel page) : base(page, "Registry") => Hive = Hives[2];

    public static IReadOnlyList<HiveOption> Hives { get; } =
    [
        new(RegistryHive.ClassesRoot, "HKEY_CLASSES_ROOT"),
        new(RegistryHive.CurrentUser, "HKEY_CURRENT_USER"),
        new(RegistryHive.LocalMachine, "HKEY_LOCAL_MACHINE"),
        new(RegistryHive.Users, "HKEY_USERS"),
        new(RegistryHive.CurrentConfig, "HKEY_CURRENT_CONFIG"),
        new(RegistryHive.DynData, "HKEY_DYN_DATA"),
    ];

    public ObservableCollection<string> Subkeys { get; } = new();
    public ObservableCollection<RegistryValueRow> Values { get; } = new();

    [ObservableProperty] public partial HiveOption Hive { get; set; }
    [ObservableProperty] public partial string Path { get; set; } = @"Software\Microsoft\Windows\CurrentVersion";
    [ObservableProperty] public partial string? SelectedSubkey { get; set; }
    [ObservableProperty] public partial bool Truncated { get; set; }

    public override string? Unavailable => Page.Agent.Capabilities != 0 && !Page.Agent.Has(Capabilities.Registry)
        ? "The registry browser needs agent 0.13.0 or later."
        : null;

    protected override Task OnFirstShownAsync() => Unavailable is null ? Go() : Task.CompletedTask;

    [RelayCommand]
    Task Go() => Run("Reading…", async () =>
    {
        var path = Path.Trim().Trim('\\');
        var result = await UseAsync(s => s.RegQueryAsync(Hive.Hive, path));
        Path = path;
        Truncated = result.Truncated;
        Subkeys.Clear();
        foreach (var k in result.Subkeys.Order(StringComparer.OrdinalIgnoreCase)) Subkeys.Add(k);
        Values.Clear();
        foreach (var v in result.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)) Values.Add(new RegistryValueRow(v));
    });

    [RelayCommand]
    Task Enter(string? subkey)
    {
        subkey ??= SelectedSubkey;
        if (subkey is null) return Task.CompletedTask;
        Path = Path.Trim().Trim('\\') is { Length: > 0 } p ? p + "\\" + subkey : subkey;
        return Go();
    }

    [RelayCommand]
    Task Up()
    {
        var p = Path.Trim().Trim('\\');
        var cut = p.LastIndexOf('\\');
        Path = cut < 0 ? "" : p[..cut];
        return Go();
    }

    partial void OnHiveChanged(HiveOption value)
    {
        if (Busy is null && Subkeys.Count + Values.Count > 0)
        {
            Path = "";
            _ = Go();
        }
    }
}

public sealed record WindowRow(WindowEntry Entry)
{
    public string Title => Entry.Title.Length == 0 ? "(untitled)" : Entry.Title;
    public string Class => Entry.ClassName;
    public string Bounds => $"{Entry.Left},{Entry.Top}  {Entry.Width}×{Entry.Height}";
    public string State => Entry.IsForeground ? "foreground" : Entry.IsMinimized ? "minimized" : "";
    public string Pid => $"{Entry.Pid:X8}";
}

/// <summary>Visible top-level windows (agent 0.13.0+); a window can be captured on its own.</summary>
public sealed partial class WindowsTab(AgentPageViewModel page) : AgentTabViewModel(page, "Windows")
{
    public ObservableCollection<WindowRow> Rows { get; } = new();

    [ObservableProperty] public partial WindowRow? Selected { get; set; }
    [ObservableProperty] public partial Avalonia.Media.Imaging.Bitmap? Capture { get; set; }

    public override string? Unavailable => Page.Agent.Capabilities != 0 && !Page.Agent.Has(Capabilities.Windows)
        ? "The window list needs agent 0.13.0 or later."
        : null;

    protected override Task OnFirstShownAsync() => Unavailable is null ? Refresh() : Task.CompletedTask;

    [RelayCommand]
    Task Refresh() => Run("Listing…", async () =>
    {
        var result = await UseAsync(s => s.WindowListAsync());
        Rows.Clear();
        foreach (var w in result.Windows) Rows.Add(new WindowRow(w));
    });

    /// <summary>Screenshot of just the selected window, cropped by the agent.</summary>
    [RelayCommand]
    Task CaptureWindow(WindowRow? row)
    {
        row ??= Selected;
        if (row is null || row.Entry.IsMinimized) return Task.CompletedTask;
        var e = row.Entry;
        return Run("Capturing…", async () =>
        {
            var options = new ScreenshotOptions(ScreenshotFlags.Compact, (uint)Math.Max(0, e.Left), (uint)Math.Max(0, e.Top),
                (uint)Math.Max(1, e.Width), (uint)Math.Max(1, e.Height));
            var (image, _) = await AgentOps.CaptureAsync(Page.Services.Backend.Agents.Connector, Page.Agent.Host, Page.Agent.Port, options);
            Capture = Ui.ToBitmap(image);
        });
    }
}
