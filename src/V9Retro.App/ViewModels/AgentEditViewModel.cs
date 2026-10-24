using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.App.Views;
using V9Retro.Core.Formatting;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

/// <summary>Add / edit agent, with the plugin's Test connection button (HELLO + INFO).</summary>
public sealed partial class AgentEditViewModel : ViewModelBase
{
    readonly AppServices _services;
    readonly Agent? _existing;
    string? _installId;

    AgentEditViewModel(AppServices services, Agent? existing)
    {
        _services = services;
        _existing = existing;
        if (existing is not null)
        {
            Name = existing.Name;
            Host = existing.Host;
            Port = existing.Port;
            Enabled = existing.Enabled;
            Notes = existing.Notes ?? "";
            VncPort = existing.VncPort;
            HasVncPassword = existing.VncPassword is not null;
            _installId = existing.InstallId;
        }
    }

    public bool IsNew => _existing is null;
    public string Title => IsNew ? "Add agent" : $"Edit {_existing!.Name}";
    public string? InstallId => _installId;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial decimal? Port { get; set; } = 9869;
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string Notes { get; set; } = "";
    [ObservableProperty] public partial decimal? VncPort { get; set; } = 5900;
    [ObservableProperty] public partial string VncPassword { get; set; } = "";
    [ObservableProperty] public partial bool HasVncPassword { get; set; }
    [ObservableProperty] public partial bool ClearVncPassword { get; set; }
    [ObservableProperty] public partial string? TestResult { get; set; }
    [ObservableProperty] public partial bool TestOk { get; set; }
    [ObservableProperty] public partial bool Testing { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    public event Action<Agent?>? CloseRequested;

    public static async Task<Agent?> ShowAddAsync(AppServices services, string? name = null, string? host = null, int? port = null,
        string? installId = null)
    {
        var vm = new AgentEditViewModel(services, null)
        {
            Name = name ?? "",
            Host = host ?? "",
            Port = port ?? 9869,
        };
        vm._installId = installId;
        return await ShowAsync(vm);
    }

    public static Task<Agent?> ShowEditAsync(AppServices services, Agent agent, MainWindowViewModel main)
    {
        var vm = new AgentEditViewModel(services, agent);
        vm.Deleted += () =>
        {
            var page = main.OpenAgents.FirstOrDefault(p => p.AgentId == agent.Id);
            if (page is not null) main.CloseAgentPage(page);
        };
        return ShowAsync(vm);
    }

    event Action? Deleted;

    static async Task<Agent?> ShowAsync(AgentEditViewModel vm)
    {
        var owner = Ui.MainWindow;
        if (owner is null) return null;
        var window = new AgentEditWindow { DataContext = vm };
        vm.CloseRequested += a => window.Close(a);
        return await window.ShowDialog<Agent?>(owner);
    }

    bool Validate(out string host, out int port)
    {
        host = Host.Trim();
        port = (int)(Port ?? 0);
        if (host.Length == 0 || host.Contains(' ')) { Error = "Enter the agent's IP address or host name."; return false; }
        if (port is < 1 or > 65535) { Error = "Port must be 1 to 65535."; return false; }
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            Error = "That is not a valid address or host name.";
            return false;
        }
        Error = null;
        return true;
    }

    [RelayCommand]
    async Task Test()
    {
        if (!Validate(out var host, out var port)) return;
        Testing = true;
        TestResult = null;
        try
        {
            var t = await AgentOps.TestConnectionAsync(_services.Backend.Agents.Connector, host, port);
            var i = t.Info;
            TestOk = true;
            TestResult = $"Connected in {t.RoundTripMs} ms\n{i.ComputerName} · {V9xFormat.OsName(i.WindowsVersion)}\n"
                         + $"Agent {i.AgentVersion} ({i.BuildId}) · up {V9xFormat.Duration(i.UptimeMs)}"
                         + (i.InstallId is { } id ? $"\nInstall {id}" : "\nNo install ID (agent older than 0.8.0)")
                         + (i.Capabilities.Has(Capabilities.Inventory) ? "" : "\nThis agent cannot report inventory (needs 0.7.0+).");
            if (string.IsNullOrWhiteSpace(Name)) Name = i.ComputerName;
            _installId = i.InstallId ?? _installId;
            if (IsNew && await _services.Backend.Agents.FindExistingAsync(i.InstallId, host, port) is { } dup)
            {
                TestOk = false;
                TestResult += $"\nAlready saved as \"{dup.Name}\".";
            }
        }
        catch (V9xException ex)
        {
            TestOk = false;
            TestResult = ex.Message;
        }
        finally
        {
            Testing = false;
        }
    }

    [RelayCommand]
    async Task Save()
    {
        if (!Validate(out var host, out var port)) return;
        var name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim();
        Agent saved;
        if (_existing is null)
        {
            if (await _services.Backend.Agents.FindExistingAsync(_installId, host, port) is { } dup)
            {
                Error = $"This machine is already saved as \"{dup.Name}\".";
                return;
            }
            saved = await _services.Backend.Agents.AddAsync(name, host, port, Notes, _installId, Enabled);
            var vncPort = (int)(VncPort ?? 5900);
            if (vncPort != 5900 || VncPassword.Length > 0)
                saved = await _services.Backend.Agents.UpdateAsync(saved.Id, name, host, port, Enabled, Notes, vncPort, VncPassword, VncPassword.Length > 0);
        }
        else
        {
            var change = ClearVncPassword || VncPassword.Length > 0;
            saved = await _services.Backend.Agents.UpdateAsync(_existing.Id, name, host, port, Enabled, Notes, (int)(VncPort ?? 5900),
                ClearVncPassword ? null : VncPassword, change);
        }
        CloseRequested?.Invoke(saved);
        // Poll right away so the page opens with fresh data, as the plugin does after saving.
        try
        {
            var poll = await _services.Backend.Agents.PollAsync(saved.Id);
            if (poll.NeedsInventory) await _services.Backend.Agents.RefreshInventoryAsync(saved.Id);
        }
        catch (Exception)
        {
            // Recorded on the agent.
        }
    }

    [RelayCommand]
    async Task Delete()
    {
        if (_existing is null) return;
        if (!await Ui.ConfirmAsync("Delete agent", $"Delete \"{_existing.Name}\" with all its screenshots, inventory and history?",
                "Delete", danger: true)) return;
        Deleted?.Invoke();
        await _services.Backend.Agents.DeleteAsync(_existing.Id);
        CloseRequested?.Invoke(null);
    }

    [RelayCommand]
    void Cancel() => CloseRequested?.Invoke(null);
}
