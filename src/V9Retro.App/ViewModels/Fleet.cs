using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using V9Retro.App.Services;
using V9Retro.Remote;
using V9Retro.Core.Formatting;
using V9Retro.Core.Model;

namespace V9Retro.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Display wrapper over one agent row; <see cref="Update"/> refreshes every binding.</summary>
public sealed partial class AgentRowViewModel(Agent agent) : ViewModelBase
{
    public Agent Agent { get; private set; } = agent;

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }

    public long Id => Agent.Id;
    public string Name => Agent.Name;
    public string Address => Agent.Address;
    public string Status => Agent.Status;
    public string StatusText => Agent.IdentityConflict is not null ? "Conflict" : V9xFormat.StatusLabel(Agent.Status);
    public bool Enabled => Agent.Enabled;
    public string Computer => Agent.ComputerName ?? "";
    public string Os => Agent.OsName ?? "";
    public string Version => Agent.AgentVersion ?? "";
    public string Cpu => Agent.CpuSummary ?? "";
    public string Ram => V9xFormat.Ram(Agent.RamTotal);
    public string Uptime => Agent.IsOnline ? V9xFormat.Duration(Agent.UptimeMs) : "";
    public string LastSeen => V9xFormat.Ago(Agent.LastSeen);
    public long LastSeenSort => Agent.LastSeen ?? 0;
    public int StatusOrder => Agent.Status switch { "online" => 0, "rebooting" => 1, "unknown" => 2, "shutdown" => 3, _ => 4 };

    public bool HasDisk => Agent.DiskTotal is > 0;
    public double DiskPercent => Agent.DiskTotal is > 0 ? 100.0 * (Agent.DiskTotal.Value - (Agent.DiskFree ?? 0)) / Agent.DiskTotal.Value : 0;
    public string Disk => Agent.DiskTotal is > 0
        ? $"{V9xFormat.Bytes(Agent.DiskTotal - Agent.DiskFree)} / {V9xFormat.Bytes(Agent.DiskTotal)}"
        : "";

    /// <summary>The dashboard card's second line: OS (or address), then uptime or last seen.</summary>
    public string Subtitle => (Agent.OsName is { Length: > 0 } os ? os : Agent.Address)
                              + " · " + (Agent.IsOnline ? "up " + V9xFormat.Duration(Agent.UptimeMs) : "seen " + V9xFormat.Ago(Agent.LastSeen));

    public string Search => $"{Name} {Address} {Computer} {Os} {Agent.InstallId}".ToLowerInvariant();

    public void Update(Agent a)
    {
        Agent = a;
        OnPropertyChanged((string?)null);
    }
}

/// <summary>The app's live list of saved agents, kept in step with the backend's change feed.
/// Shared by the dashboard, the agents table, discovery and the command palette. Loads
/// asynchronously (the backend may be a remote host) and reloads when a remote connection returns.</summary>
public sealed class Fleet
{
    readonly IControllerBackend _backend;
    readonly Dictionary<long, AgentRowViewModel> _byId = new();

    public Fleet(IControllerBackend backend)
    {
        _backend = backend;
        var feed = backend.Feed;
        feed.AgentAdded += a => Ui.Post(() => { if (!_byId.ContainsKey(a.Id)) AddRow(a); Changed?.Invoke(); });
        feed.AgentChanged += a => Ui.Post(() =>
        {
            if (_byId.TryGetValue(a.Id, out var row)) row.Update(a);
            Changed?.Invoke();
        });
        feed.AgentRemoved += id => Ui.Post(() =>
        {
            if (_byId.Remove(id, out var row)) Rows.Remove(row);
            Changed?.Invoke();
        });
        feed.ScreenshotAdded += (agentId, shotId) => Ui.Post(() =>
        {
            if (_byId.TryGetValue(agentId, out var row)) _ = LoadThumbnailAsync(row, shotId);
        });
        backend.ConnectionChanged += up => Ui.Post(() =>
        {
            if (up) _ = ReloadAsync();
        });
        _ = ReloadAsync();
    }

    public ObservableCollection<AgentRowViewModel> Rows { get; } = new();

    /// <summary>Raised on the UI thread after any add, change or removal.</summary>
    public event Action? Changed;

    public AgentRowViewModel? Find(long id) => _byId.GetValueOrDefault(id);

    public async Task ReloadAsync()
    {
        IReadOnlyList<Agent> agents;
        try { agents = await _backend.Agents.AllAsync(); }
        catch (Exception) { return; }
        var keep = agents.Select(a => a.Id).ToHashSet();
        foreach (var gone in _byId.Keys.Where(id => !keep.Contains(id)).ToList())
            if (_byId.Remove(gone, out var row)) Rows.Remove(row);
        foreach (var a in agents)
        {
            if (_byId.TryGetValue(a.Id, out var row)) row.Update(a);
            else AddRow(a);
        }
        Changed?.Invoke();
    }

    async Task LoadThumbnailAsync(AgentRowViewModel row, long shotId)
    {
        try { row.Thumbnail = Ui.FromPng(await _backend.Agents.ThumbnailAsync(shotId)); }
        catch (Exception) { /* keep the old one */ }
    }

    void AddRow(Agent a)
    {
        var row = new AgentRowViewModel(a);
        if (a.LastScreenshotId is { } shot) _ = LoadThumbnailAsync(row, shot);
        _byId[a.Id] = row;
        var at = 0;
        while (at < Rows.Count && string.Compare(Rows[at].Name, a.Name, StringComparison.OrdinalIgnoreCase) < 0) at++;
        Rows.Insert(at, row);
    }
}
