using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Services;
using V9Retro.Core.Formatting;
using V9Retro.Core.Imaging;
using V9Retro.Core.Model;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

public sealed record ShotThumb(long Id, Bitmap? Image, string Caption);

/// <summary>The plugin's agent page: latest screen with Live mode and history strip, the
/// System card and recent activity.</summary>
public sealed partial class OverviewTab : AgentTabViewModel
{
    CancellationTokenSource? _live;
    byte[]? _lastPng;

    public OverviewTab(AgentPageViewModel page) : base(page, "Overview")
    {
        BuildSystem();
        _ = LoadStoredAsync();
    }

    /// <summary>Reloads the history after the page's agent was saved.</summary>
    public Task ReloadAsync() => LoadStoredAsync();

    public static IReadOnlyList<int> LiveIntervals { get; } = [15, 30, 60];

    [ObservableProperty] public partial Bitmap? Screen { get; set; }
    [ObservableProperty] public partial string? ScreenCaption { get; set; }
    [ObservableProperty] public partial bool Live { get; set; }
    [ObservableProperty] public partial int LiveSeconds { get; set; } = 30;

    public ObservableCollection<ShotThumb> History { get; } = new();
    public ObservableCollection<InfoRow> System { get; } = new();

    public bool CanScreenshot => Page.Agent.CanScreenshot;

    public override void AgentChanged()
    {
        base.AgentChanged();
        OnPropertyChanged(nameof(CanScreenshot));
        BuildSystem();
        if (!CanScreenshot && Live) StopLive();
    }

    IReadOnlyList<ScreenshotRecord> _shots = [];

    /// <summary>Loads the stored history (thumbnails) and shows the newest, or <paramref name="show"/>.</summary>
    async Task LoadStoredAsync(long? show = null)
    {
        if (!Page.IsSaved) return;
        var agents = Page.Services.Backend.Agents;
        try
        {
            _shots = await agents.ScreenshotsAsync(Page.AgentId);
            var thumbs = await Task.WhenAll(_shots.Select(s => agents.ThumbnailAsync(s.Id)));
            History.Clear();
            for (var i = 0; i < _shots.Count; i++)
                History.Add(new ShotThumb(_shots[i].Id, Ui.FromPng(thumbs[i]), $"{V9xFormat.Ago(_shots[i].DateAdded)} · {_shots[i].Source}"));
            if ((show ?? _shots.FirstOrDefault()?.Id) is { } id) await ShowStoredAsync(id);
        }
        catch (Exception ex)
        {
            Error = Ui.Describe(ex);
        }
    }

    async Task ShowStoredAsync(long id)
    {
        var shot = _shots.FirstOrDefault(s => s.Id == id);
        if (shot is null) return;
        _lastPng = await Page.Services.Backend.Agents.ScreenshotPngAsync(id);
        Screen = Ui.FromPng(_lastPng);
        ScreenCaption = $"{shot.Width}×{shot.Height}, {shot.Bpp}-bit · {Clockless(shot.DateAdded)}";
    }

    static string Clockless(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("g");

    void BuildSystem()
    {
        var a = Page.Agent;
        System.Clear();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value)) System.Add(new InfoRow(label, value));
        }
        Add("Computer", a.ComputerName);
        Add("Operating system", a.OsName ?? a.WindowsVersion);
        Add("Agent", a.AgentVersion is null ? null : $"{a.AgentVersion} ({a.BuildId})");
        Add("Install ID", a.InstallId);
        Add("Uptime", a.IsOnline ? V9xFormat.Duration(a.UptimeMs) : null);
        Add("Boot counter", a.BootCounter?.ToString());
        Add("Screen", a.ScreenWidth is > 0 ? $"{a.ScreenWidth}×{a.ScreenHeight}, {a.ScreenBpp}-bit{(a.DesktopReady ? "" : " (desktop not ready)")}" : null);
        Add("CPU", a.CpuSummary);
        Add("Memory", a.RamTotal is null ? null : V9xFormat.Ram(a.RamTotal));
        Add("Winsock", a.WinsockVersion is null ? null : V9xFormat.Winsock(a.WinsockVersion));
        Add("Windows folder", a.WindowsDir);
        Add("Listening on", string.IsNullOrEmpty(a.ListenAddress) ? null : $"{a.ListenAddress}:{a.Port}");
        Add("Allowed client", a.AllowedClient is { Length: > 0 } c ? c : a.Capabilities != 0 ? "any" : null);
        Add("Capabilities", a.Capabilities == 0 ? null : string.Join(", ", Enum.GetValues<Capabilities>()
            .Where(c => c != Capabilities.None && a.Has(c)).Select(c => c.ToString())));
        Add("Notes", a.Notes);
    }

    [RelayCommand]
    Task TakeScreenshot() => Run("Capturing…", CaptureAsync);

    async Task CaptureAsync()
    {
        try
        {
            if (Page.IsSaved)
            {
                var shot = await Page.Services.Backend.Agents.TakeScreenshotAsync(Page.AgentId, Live ? "live" : "manual");
                await LoadStoredAsync(shot);
            }
            else
            {
                var (image, info) = await AgentOps.CaptureAsync(Page.Services.Backend.Agents.Connector, Page.Agent.Host, Page.Agent.Port);
                _lastPng = PngEncoder.Encode(image);
                Screen = Ui.ToBitmap(image);
                ScreenCaption = $"{info.Width}×{info.Height}, {info.SourceBitsPerPixel}-bit · just now (not saved)";
            }
        }
        catch (V9xGuestException ex) when (ex.Status == V9xStatus.Busy)
        {
            Error = "Busy: another capture or a program started through the agent is running.";
        }
    }

    [RelayCommand]
    void SelectHistory(ShotThumb? thumb)
    {
        if (thumb is not null) _ = ShowStoredAsync(thumb.Id);
    }

    [RelayCommand]
    void OpenFull()
    {
        if (Screen is { } s) Ui.ShowImage($"{Page.Name}: screen", s, new Size(s.PixelSize.Width, s.PixelSize.Height));
    }

    [RelayCommand]
    async Task SavePng()
    {
        if (_lastPng is null || Ui.Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save screenshot",
            SuggestedFileName = $"{Page.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(_lastPng);
    }

    partial void OnLiveChanged(bool value)
    {
        if (value) StartLive();
        else StopLive();
    }

    void StartLive()
    {
        if (_live is not null) return;
        _live = new CancellationTokenSource();
        _ = LiveLoopAsync(_live.Token);
    }

    public void StopLive()
    {
        _live?.Cancel();
        _live = null;
        if (Live) Live = false;
    }

    /// <summary>One capture at a time: the next starts after the previous finished plus the interval.</summary>
    async Task LiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (CanScreenshot) await Run("Capturing…", CaptureAsync);
            try { await Task.Delay(TimeSpan.FromSeconds(LiveSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
