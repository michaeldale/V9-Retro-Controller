using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Formatting;
using V9Retro.Core.Services;
using V9Retro.Remote;
using V9Retro.Remote.Client;
using V9Retro.Remote.Server;

namespace V9Retro.App.ViewModels;

public sealed partial class IntSettingViewModel(AppSettings settings, AppSettings.IntSetting setting) : ViewModelBase
{
    public string Label => setting.Label;
    public string Help => setting.Help;
    public decimal Min => setting.Min;
    public decimal Max => setting.Max;

    public decimal? Value
    {
        get => settings.Get(setting);
        set
        {
            if (value is null) return;
            settings.Set(setting, (int)value.Value);
            OnPropertyChanged();
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Value));
}

public sealed record TokenRow(string Id, string Name, string Created, string LastUsed);

public sealed partial class SettingsViewModel : ViewModelBase
{
    readonly AppServices _services;

    public SettingsViewModel(AppServices services, UpdatesViewModel updates)
    {
        _services = services;
        Updates = updates;
        Numbers = AppSettings.IntSettings.Select(s => new IntSettingViewModel(services.Backend.Settings, s)).ToList();
        services.Backend.Settings.Changed += () => Ui.Post(() =>
        {
            foreach (var n in Numbers) n.Refresh();
            OnPropertyChanged(nameof(DiscoveryAutoBrowse));
        });
        if (Server is { } server)
        {
            server.StateChanged += () => Ui.Post(UpdateServerStatus);
            UpdateServerStatus();
            LoadTokens();
        }
    }

    ApiServer? Server => _services.Backend.Server;

    public UpdatesViewModel Updates { get; }

    public IReadOnlyList<IntSettingViewModel> Numbers { get; }

    public bool DiscoveryAutoBrowse
    {
        get => _services.Backend.Settings.DiscoveryAutoBrowse;
        set
        {
            _services.Backend.Settings.DiscoveryAutoBrowse = value;
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _services.Backend.Settings.CloseToTray;
        set
        {
            _services.Backend.Settings.CloseToTray = value;
            OnPropertyChanged();
        }
    }

    public string DataDirectory => _services.Backend.DataDirectory ?? "";
    public bool HasDataDirectory => _services.Backend.DataDirectory is not null;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    // ---- connection (local or remote) ------------------------------------------------

    public bool IsRemote => _services.Backend.IsRemote;
    public bool IsHost => Server is not null;
    public string ModeText => IsRemote
        ? $"Connected to {_services.Backend.Location}. Agents, history and settings live on that host, and every agent connection goes through it."
        : "Using this computer's database.";

    [RelayCommand]
    async Task ConnectToHost()
    {
        var text = await Ui.PromptAsync("Connect to a host",
            "Paste the pairing code from the host (Settings → Remote access → Add client on the Windows PC):", confirm: "Connect");
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!PairingCode.TryDecode(text, out var code))
        {
            await Ui.AlertAsync("Connect to a host", "That is not a pairing code. It starts with v9r1.");
            return;
        }
        var url = await Ui.PromptAsync("Host address", "The address this computer uses to reach the host:", code!.Url, confirm: "Connect");
        if (string.IsNullOrWhiteSpace(url)) return;
        code = code with { Url = url.Trim() };
        try
        {
            var probe = await RemoteBackend.ConnectAsync(code.Url, code.Fingerprint, code.Token, new ClientConfig());
            var where = probe.Location;
            await probe.DisposeAsync();
            if (!await Ui.ConfirmAsync("Connect to a host",
                    $"Connected to {where}. Restart the app to work against that host? This computer's own agents stay in its database for when you switch back.",
                    "Restart and connect")) return;
        }
        catch (Exception ex)
        {
            await Ui.AlertAsync("Cannot connect", Ui.Describe(ex));
            return;
        }
        _services.Config.UsePairing(code, _services.Secrets);
        _services.Config.Save();
        _services.Restart?.Invoke();
    }

    [RelayCommand]
    async Task UseThisComputer()
    {
        if (!await Ui.ConfirmAsync("Use this computer", "Disconnect from the host and restart using this computer's own database?", "Restart"))
            return;
        _services.Config.Mode = "local";
        _services.Config.Save();
        _services.Restart?.Invoke();
    }

    // ---- remote access (host) --------------------------------------------------------

    public ObservableCollection<TokenRow> Tokens { get; } = new();

    [ObservableProperty] public partial string? ServerStatus { get; set; }
    [ObservableProperty] public partial bool ServerBusy { get; set; }

    public bool ServerEnabled
    {
        get => Server?.Enabled ?? false;
        set
        {
            if (Server is null || Server.Enabled == value) return;
            Server.Enabled = value;
            OnPropertyChanged();
            _ = ApplyServerAsync();
        }
    }

    public decimal? ServerPort
    {
        get => Server?.Port;
        set
        {
            if (Server is null || value is null || (int)value == Server.Port) return;
            Server.Port = (int)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Urls));
            if (Server.IsRunning) _ = RestartServerAsync();
        }
    }

    public string Fingerprint => Server is { IsRunning: true } s
        ? string.Join(" ", Enumerable.Range(0, s.Fingerprint.Length / 4).Select(i => s.Fingerprint.Substring(i * 4, 4)))
        : "";

    public string Urls => Server is null ? "" : string.Join("\n", Server.Urls);

    async Task ApplyServerAsync()
    {
        if (Server is null) return;
        ServerBusy = true;
        try
        {
            if (Server.Enabled) await Server.StartAsync();
            else await Server.StopAsync();
        }
        catch (Exception)
        {
            // Shown by UpdateServerStatus via LastError.
        }
        finally
        {
            ServerBusy = false;
            UpdateServerStatus();
        }
    }

    async Task RestartServerAsync()
    {
        if (Server is null) return;
        await Server.StopAsync();
        await ApplyServerAsync();
    }

    void UpdateServerStatus()
    {
        if (Server is null) return;
        ServerStatus = Server.IsRunning ? $"Running on port {Server.Port}."
            : Server.LastError is { } e ? "Not running: " + e
            : "Off.";
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(ServerEnabled));
    }

    void LoadTokens()
    {
        if (Server is null) return;
        Tokens.Clear();
        foreach (var t in Server.Tokens.All().OrderBy(t => t.CreatedAt))
            Tokens.Add(new TokenRow(t.Id, t.Name, V9xFormat.Ago(t.CreatedAt), t.LastUsedAt is { } u ? V9xFormat.Ago(u) : "never"));
    }

    [RelayCommand]
    async Task AddClient()
    {
        if (Server is null) return;
        var name = await Ui.PromptAsync("Add client", "A name for the computer that will connect (shown in activity, e.g. \"MacBook\"):",
            confirm: "Create code");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!Server.IsRunning)
        {
            Server.Enabled = true;
            await ApplyServerAsync();
            OnPropertyChanged(nameof(ServerEnabled));
            if (!Server.IsRunning)
            {
                await Ui.AlertAsync("Remote access", ServerStatus ?? "The server did not start.");
                return;
            }
        }
        var (_, code) = Server.CreatePairing(name);
        LoadTokens();
        await Ui.ShowCodeAsync("Pairing code for " + name.Trim(),
            "On the other computer open Settings → Connection → Connect to a host and paste this code. It contains this PC's address, "
            + "its certificate fingerprint and a new token. It is shown only once; revoke the token here to cut that computer off.",
            code);
    }

    [RelayCommand]
    async Task Revoke(TokenRow? row)
    {
        if (Server is null || row is null) return;
        if (!await Ui.ConfirmAsync("Revoke token", $"Revoke \"{row.Name}\"? That computer loses access at its next request.", "Revoke", danger: true))
            return;
        Server.Tokens.Revoke(row.Id);
        LoadTokens();
    }

    [RelayCommand]
    void OpenDataDirectory()
    {
        if (!HasDataDirectory) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = DataDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = Ui.AlertAsync("Open folder", ex.Message);
        }
    }
}
