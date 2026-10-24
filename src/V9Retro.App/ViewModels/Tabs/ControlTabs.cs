using System.Net.Sockets;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Services;
using V9Retro.Protocol;
using V9Retro.Vnc;

namespace V9Retro.App.ViewModels;

/// <summary>Drive the guest through the agent's INPUT request when VNC is not available: click
/// on a fresh screenshot, type text, send keys. The screen refreshes after each action.</summary>
public sealed partial class ControlTab(AgentPageViewModel page) : AgentTabViewModel(page, "Control")
{
    // Win32 virtual-key codes.
    const byte VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkControl = 0x11, VkMenu = 0x12, VkEscape = 0x1B,
        VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkDelete = 0x2E, VkF4 = 0x73;

    [ObservableProperty] public partial Bitmap? Screen { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial bool AutoRefresh { get; set; } = true;
    [ObservableProperty] public partial string? Cursor { get; set; }

    public override string? Unavailable => Page.Agent.Capabilities != 0 && !Page.Agent.Has(Capabilities.InputInject)
        ? "This agent does not accept input."
        : !Page.Agent.DesktopReady && Page.Agent.IsOnline ? "The desktop is not ready yet (after a reboot, wait for logon)." : null;

    protected override Task OnFirstShownAsync() => Page.Agent.CanScreenshot ? RefreshScreen() : Task.CompletedTask;

    [RelayCommand]
    Task RefreshScreen() => Run("Capturing…", CaptureAsync);

    async Task CaptureAsync()
    {
        var (image, _) = await AgentOps.CaptureAsync(Page.Services.Backend.Agents.Connector, Page.Agent.Host, Page.Agent.Port);
        Screen = Ui.ToBitmap(image);
    }

    Task SendAsync(IEnumerable<InputAction> actions) => Run("Sending…", async () =>
    {
        var batch = actions.ToList();
        if (AutoRefresh) batch.Add(new InputAction.Delay(350));
        var result = await UseAsync(s => s.InputAsync(batch));
        Cursor = $"cursor {result.CursorX},{result.CursorY}";
        if (AutoRefresh && Page.Agent.CanScreenshot) await CaptureAsync();
    });

    /// <summary>Called by the view with screen-pixel coordinates.</summary>
    public Task ClickAt(int x, int y, MouseButton button, bool doubleClick)
    {
        var actions = InputAction.Click(x, y, button).ToList();
        if (doubleClick) actions.AddRange(InputAction.Click(x, y, button).Skip(1));
        return SendAsync(actions);
    }

    public Task Scroll(int x, int y, int notches) =>
        SendAsync([new InputAction.MoveTo(x, y), new InputAction.Wheel((short)notches)]);

    [RelayCommand]
    Task TypeText()
    {
        var text = Text;
        if (text.Length == 0) return Task.CompletedTask;
        if (text.Any(c => c > 0x7F))
        {
            Error = "Only plain ASCII can be typed through the agent.";
            return Task.CompletedTask;
        }
        Text = "";
        return SendAsync([new InputAction.TypeText(text)]);
    }

    [RelayCommand]
    Task Key(string? name) => name switch
    {
        "enter" => SendAsync(InputAction.Chord(VkReturn)),
        "esc" => SendAsync(InputAction.Chord(VkEscape)),
        "tab" => SendAsync(InputAction.Chord(VkTab)),
        "backspace" => SendAsync(InputAction.Chord(VkBack)),
        "delete" => SendAsync(InputAction.Chord(VkDelete)),
        "start" => SendAsync(InputAction.Chord(VkControl, VkEscape)),
        "alttab" => SendAsync(InputAction.Chord(VkMenu, VkTab)),
        "altf4" => SendAsync(InputAction.Chord(VkMenu, VkF4)),
        "up" => SendAsync(InputAction.Chord(VkUp)),
        "down" => SendAsync(InputAction.Chord(VkDown)),
        "left" => SendAsync(InputAction.Chord(VkLeft)),
        "right" => SendAsync(InputAction.Chord(VkRight)),
        _ => Task.CompletedTask,
    };
}

/// <summary>Built-in VNC viewer for V9XVNC.EXE. The view (VncView) renders the client's
/// framebuffer and forwards input; this holds the connection and its state.</summary>
public sealed partial class VncTab(AgentPageViewModel page) : AgentTabViewModel(page, "VNC"), IDisposable
{
    [ObservableProperty] public partial VncClient? Client { get; set; }
    [ObservableProperty] public partial bool Connected { get; set; }
    [ObservableProperty] public partial string? Status { get; set; } = "Not connected";
    [ObservableProperty] public partial bool ScaleToFit { get; set; } = true;
    [ObservableProperty] public partial bool ViewOnly { get; set; }
    [ObservableProperty] public partial bool NotRunning { get; set; }

    public int Port => Page.Agent.VncPort > 0 ? Page.Agent.VncPort : 5900;

    [RelayCommand]
    async Task Connect()
    {
        await DisconnectAsync();
        string? password = null;
        if (Page.IsSaved)
        {
            try { password = await Page.Services.Backend.Agents.VncPasswordAsync(Page.AgentId); }
            catch (Exception) { /* prompt instead */ }
        }
        await ConnectWith(password, allowPrompt: true);
    }

    async Task ConnectWith(string? password, bool allowPrompt)
    {
        NotRunning = false;
        Error = null;
        Status = $"Connecting to {Page.Agent.Host}:{Port}…";
        var client = new VncClient();
        try
        {
            // Through the connector, so a remote client reaches VNC via the host's tunnel.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var stream = await Page.Services.Backend.Agents.Connector.OpenRawAsync(Page.Agent.Host, Port, TimeSpan.FromSeconds(5), timeout.Token);
            await client.ConnectAsync(stream, password, timeout.Token);
        }
        catch (VncAuthenticationException ex) when (allowPrompt)
        {
            await client.DisposeAsync();
            var entered = await Ui.PromptAsync("VNC password",
                ex.PasswordRequired ? $"{Page.Name}'s VNC server needs a password." : "That password was refused. Try again:",
                password: true, confirm: "Connect");
            if (entered is null)
            {
                Status = "Not connected";
                return;
            }
            await ConnectWith(entered, allowPrompt: true);
            if (Connected && Page.IsSaved
                && await Ui.ConfirmAsync("Remember password", "Save this VNC password for this agent (encrypted for your Windows account)?", "Save"))
            {
                var a = Page.Agent;
                Page.Agent = await Page.Services.Backend.Agents.UpdateAsync(a.Id, a.Name, a.Host, a.Port, a.Enabled, a.Notes, a.VncPort, entered, true);
            }
            return;
        }
        catch (VncViewerBusyException)
        {
            await client.DisposeAsync();
            Status = "Another viewer is connected (V9XVNC serves one at a time).";
            return;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or V9xTransportException)
        {
            await client.DisposeAsync();
            NotRunning = true;
            Status = ex is V9xTransportException ? $"No VNC server answers on port {Port} ({ex.Message})." : $"No VNC server answers on port {Port}.";
            return;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            Status = Ui.Describe(ex);
            return;
        }

        client.Disconnected += e => Ui.Post(() =>
        {
            if (!ReferenceEquals(Client, client)) return;
            Connected = false;
            Status = e is null ? "Disconnected" : "Disconnected: " + e.Message;
        });
        client.ServerClipboard += text => Ui.Post(() => _ = Ui.MainWindow?.Clipboard?.SetTextAsync(text));
        Client = client;
        Connected = true;
        Status = $"{client.DesktopName} · RFB 3.{client.ProtocolMinorVersion}";
    }

    [RelayCommand]
    Task Disconnect() => DisconnectAsync();

    async Task DisconnectAsync()
    {
        var client = Client;
        Client = null;
        Connected = false;
        if (client is not null)
        {
            await client.DisposeAsync();
            Status = "Not connected";
        }
    }

    [RelayCommand]
    Task CtrlAltDel() => Client?.SendCtrlAltDelAsync() ?? Task.CompletedTask;

    [RelayCommand]
    async Task SendKeys(string? which)
    {
        if (Client is not { } c) return;
        uint[] keys = which switch
        {
            "start" => [KeysymMap.ControlL, 0xFF1B],
            "alttab" => [KeysymMap.AltL, 0xFF09],
            "altf4" => [KeysymMap.AltL, 0xFFC1],
            "win" => [0xFFEB],
            _ => [],
        };
        foreach (var k in keys) await c.SendKeyAsync(k, true);
        for (var i = keys.Length - 1; i >= 0; i--) await c.SendKeyAsync(keys[i], false);
    }

    [RelayCommand]
    async Task PasteClipboard()
    {
        if (Client is not { } c || Ui.MainWindow?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) await c.SendClipboardAsync(text);
    }

    [RelayCommand]
    void PopOut()
    {
        if (Client is not null) Views.VncWindow.Open(this);
    }

    [RelayCommand]
    void GoToMaintenance() => Page.SelectTab(Page.Maintenance);

    public void Dispose() => _ = DisconnectAsync();
}

/// <summary>VNC server control (status, start/stop, enable/disable, password), agent hot update
/// from an install package, and guest-side HTTP downloads.</summary>
public sealed partial class MaintenanceTab(AgentPageViewModel page) : AgentTabViewModel(page, "Maintenance")
{
    [ObservableProperty] public partial VncStatus? Vnc { get; set; }
    [ObservableProperty] public partial string VncSummary { get; set; } = "Not checked";
    [ObservableProperty] public partial string? UpdateLog { get; set; }
    [ObservableProperty] public partial string DownloadUrl { get; set; } = "http://";
    [ObservableProperty] public partial string DownloadPath { get; set; } = @"C:\V9XREMOTE\TEMP\";
    [ObservableProperty] public partial string? DownloadResult { get; set; }

    MaintenanceService Service => Page.Services.Backend.Maintenance;

    public bool CanUpdate => Page.Agent.CanHotUpdate;
    public bool CanDownload => Page.Agent.Has(Capabilities.HttpDownload);
    public bool CanSelfUpdate => Page.Agent.IsOnline && MaintenanceService.CanSelfUpdate(Page.Agent);

    public override void AgentChanged()
    {
        base.AgentChanged();
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanSelfUpdate));
        OnPropertyChanged(nameof(ServerUpdateHint));
    }

    protected override async Task OnFirstShownAsync()
    {
        if (!Page.Agent.IsOnline) return;
        await RefreshVnc();
        await CheckServer();
    }

    // ---- agent update from michaeldale.com.au ---------------------------------------

    [ObservableProperty] public partial string? ServerStatus { get; set; }
    [ObservableProperty] public partial V9Retro.Core.Updates.VerifiedUpdate? ServerUpdate { get; set; }

    public bool HasServerUpdate => ServerUpdate is not null;
    public string? ServerNotes => ServerUpdate?.Notes is { Length: > 0 } n ? n : null;

    public string ServerUpdateHint => CanSelfUpdate
        ? "Update directly: the agent downloads, verifies and stages the release from michaeldale.com.au itself, then it is hot-swapped in (no reboot)."
        : $"This agent ({Page.Agent.AgentVersion ?? "unknown"}) cannot fetch updates unattended (0.13.1+), so update it through this PC: "
          + "the PC downloads and verifies the release, then uploads and hot-swaps it.";

    partial void OnServerUpdateChanged(V9Retro.Core.Updates.VerifiedUpdate? value)
    {
        OnPropertyChanged(nameof(HasServerUpdate));
        OnPropertyChanged(nameof(ServerNotes));
    }

    [RelayCommand]
    Task CheckServer() => Run("Checking michaeldale.com.au…", async () =>
    {
        ServerUpdate = null;
        try
        {
            var (check, update) = await MaintenanceService.CheckAgentUpdateAsync(Page.Agent);
            ServerUpdate = update;
            ServerStatus = update is not null
                ? $"Agent {update.Version} is available (this machine has {Page.Agent.AgentVersion}); the signed release verified."
                : check.Status switch
                {
                    "current" => $"Agent {Page.Agent.AgentVersion} is the newest version.",
                    _ => check.Message ?? $"The update server answered \"{check.Status}\".",
                };
        }
        catch (V9Retro.Core.Updates.UpdateVerificationException ex)
        {
            ServerStatus = "The server's release did not verify, so it will not be installed: " + ex.Message;
        }
        catch (HttpRequestException ex)
        {
            ServerStatus = "Cannot reach michaeldale.com.au from this PC: " + ex.Message;
        }
    });

    [RelayCommand]
    async Task UpdateDirect()
    {
        if (!await Ui.ConfirmAsync("Update agent", $"Let {Page.Name} download and install the newest agent from michaeldale.com.au itself? "
                + "It verifies the release signature first; the agent then restarts (a few seconds offline).", "Update")) return;
        UpdateLog = "";
        var progress = new Progress<string>(line => UpdateLog += line + "\n");
        await Run("Updating agent from the server…", async () =>
        {
            var r = await Service.UpdateAgentFromServerOnGuestAsync(Page.Agent, progress);
            UpdateLog += r.Message ?? $"Done: {r.PreviousBuild} → {r.Build} (boot {r.PreviousBoot} → {r.Boot})\n";
            await CheckServer();
        });
        if (Error is not null) UpdateLog += "Failed: " + Error + "\n";
    }

    [RelayCommand]
    async Task UpdateThroughPc()
    {
        if (ServerUpdate is not { } update) return;
        if (!await Ui.ConfirmAsync("Update agent", $"Download agent {update.Version} to this PC, verify it, and install it on {Page.Name}? "
                + "The agent restarts (a few seconds offline).", "Update")) return;
        UpdateLog = "";
        var progress = new Progress<string>(line => UpdateLog += line + "\n");
        await Run("Updating agent through this PC…", async () =>
        {
            var r = await Service.UpdateAgentFromServerThroughHostAsync(Page.Agent, update, progress);
            UpdateLog += $"Done: {r.PreviousBuild} → {r.Build} (boot {r.PreviousBoot} → {r.Boot})"
                         + (r.VncUpdated ? ", V9XVNC.EXE replaced" : "") + (r.SetupUpdated ? ", SETUP.EXE replaced" : "") + "\n";
            await CheckServer();
        });
        if (Error is not null) UpdateLog += "Failed: " + Error + "\n";
    }

    void Show(VncStatus s)
    {
        Vnc = s;
        VncSummary = !s.Installed
            ? "V9XVNC.EXE is not installed (agent package 0.11.0 or later)."
            : $"{(s.Running ? "Running" : "Stopped")} · {(s.Enabled ? "enabled" : "disabled")} · port {s.Port}"
              + $" · {(s.PasswordSet ? "password set" : "no password")}{(s.SetupAvailable ? "" : " · no SETUP.EXE")}";
    }

    [RelayCommand]
    Task RefreshVnc() => Run("Checking VNC…", async () => Show(await Service.VncStatusAsync(Page.Agent)));

    [RelayCommand]
    Task StartVnc() => Run("Starting VNC…", async () => Show(await Service.StartVncAsync(Page.Agent)));

    [RelayCommand]
    Task StopVnc() => Run("Stopping VNC…", async () => Show(await Service.StopVncAsync(Page.Agent)));

    [RelayCommand]
    async Task EnableVnc()
    {
        if (!await Ui.ConfirmAsync("Enable VNC remote control",
                $"Turn on VNC remote control on {Page.Name}? Anyone who can reach port {Vnc?.Port ?? 5900} can then watch and "
                + "drive this machine (with the password, if one is set). This runs SETUP.EXE /S /vnc on the guest, which also "
                + "starts the server at every logon.", "Enable", danger: true)) return;
        var password = await Ui.PromptAsync("VNC password",
            "Optional password, up to 8 characters (stored in plain text in AGENT.INI on the guest). Leave empty for none:",
            password: true, confirm: "Enable");
        if (password is null) return;
        if (password.Length > 8)
        {
            Error = "VNC passwords are at most 8 characters.";
            return;
        }
        await Run("Enabling VNC…", async () =>
        {
            Show(await Service.SetVncEnabledAsync(Page.Agent, true, password.Length > 0 ? password : null));
            if (password.Length > 0 && Page.IsSaved)
            {
                var a = Page.Agent;
                Page.Agent = await Page.Services.Backend.Agents.UpdateAsync(a.Id, a.Name, a.Host, a.Port, a.Enabled, a.Notes, a.VncPort, password, true);
            }
        });
    }

    [RelayCommand]
    async Task DisableVnc()
    {
        if (!await Ui.ConfirmAsync("Disable VNC", $"Turn off VNC remote control on {Page.Name}?", "Disable")) return;
        await Run("Disabling VNC…", async () => Show(await Service.SetVncEnabledAsync(Page.Agent, false)));
    }

    [RelayCommand]
    async Task UpdateAgent()
    {
        if (Ui.Storage is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Agent install package (folder with V9XAGNT.EXE and V9XSHOT.EXE)",
        });
        var dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (dir is null) return;
        if (!await Ui.ConfirmAsync("Update agent", $"Hot-update the agent on {Page.Name} from\n{dir}?", "Update")) return;
        UpdateLog = "";
        var progress = new Progress<string>(line => UpdateLog += line + "\n");
        await Run("Updating agent…", async () =>
        {
            var r = await Service.UpdateAgentAsync(Page.Agent, dir, progress);
            UpdateLog += $"Done: {r.PreviousBuild} → {r.Build} (boot {r.PreviousBoot} → {r.Boot})"
                         + (r.VncUpdated ? ", V9XVNC.EXE replaced" : "") + (r.SetupUpdated ? ", SETUP.EXE replaced" : "") + "\n";
        });
        if (Error is not null) UpdateLog += "Failed: " + Error + "\n";
    }

    [RelayCommand]
    Task GuestDownload() => Run("Guest downloading…", async () =>
    {
        var url = DownloadUrl.Trim();
        var path = DownloadPath.Trim();
        if (path.EndsWith('\\'))
        {
            var name = Uri.TryCreate(url, UriKind.Absolute, out var u) ? System.IO.Path.GetFileName(u.LocalPath) : "";
            path += string.IsNullOrEmpty(name) ? "DOWNLOAD.BIN" : name;
        }
        var progress = new Progress<long>(n => DownloadResult = $"{n:N0} bytes…");
        var r = await UseAsync(s => s.GuestHttpDownloadAsync(url, path, progress), TimeSpan.FromSeconds(600));
        DownloadResult = $"HTTP {r.HttpStatus}: {r.Size:N0} bytes to {path} (CRC32 {r.Crc32:X8})";
        if (Page.IsSaved) await Page.Services.Backend.Agents.LogActionAsync(Page.AgentId, $"Guest downloaded {url} to {path}");
    });
}
