using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Updates;

namespace V9Retro.App.ViewModels;

/// <summary>Update check and install against michaeldale.com.au, as the agent's SETUP.EXE /update
/// does: manual "Check for updates", an optional quiet check at startup, and Install, which only
/// runs after the release signature, version and package hash have all verified.</summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    readonly AppServices _services;

    public UpdatesViewModel(AppServices services)
    {
        _services = services;
        if (services.Backend.Settings.UpdateCheckOnStartup)
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                Ui.Post(() => _ = CheckCoreAsync(quiet: true));
            });
    }

    public string CurrentVersion => UpdateClient.CurrentVersion;
    public string Family => UpdateClient.Family;
    public bool CanInstall => UpdateInstaller.CanInstall;
    public string VersionText => $"Version {CurrentVersion} ({Family}){(CanInstall ? "" : " · development build")}";

    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial VerifiedUpdate? Available { get; set; }

    public bool HasUpdate => Available is not null;
    public string? AvailableText => Available is { } u ? $"Version {u.Version} is available" : null;
    public string? Notes => Available?.Notes is { Length: > 0 } n ? n : null;

    /// <summary>Raised on the UI thread when a quiet startup check finds an update.</summary>
    public event Action<VerifiedUpdate>? Found;

    partial void OnAvailableChanged(VerifiedUpdate? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(AvailableText));
        OnPropertyChanged(nameof(Notes));
    }

    public bool CheckOnStartup
    {
        get => _services.Backend.Settings.UpdateCheckOnStartup;
        set
        {
            _services.Backend.Settings.UpdateCheckOnStartup = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    Task Check() => CheckCoreAsync(quiet: false);

    async Task CheckCoreAsync(bool quiet)
    {
        if (Busy) return;
        Busy = true;
        if (!quiet) Status = "Checking michaeldale.com.au…";
        try
        {
            using var client = new UpdateClient();
            var check = await client.CheckAsync();
            switch (check.Status)
            {
                case "update":
                    var verified = await client.VerifyAsync(check);
                    Available = verified;
                    Status = $"{verified.Version} is available{(check.Released is { } r ? $" (released {r})" : "")}. The signature and package checks passed.";
                    if (quiet) Found?.Invoke(verified);
                    break;
                case "current":
                    Available = null;
                    Status = $"You have the latest version ({CurrentVersion}).";
                    break;
                case "no_release" or "no_package":
                    Available = null;
                    Status = check.Message ?? "No release has been published for this platform yet.";
                    break;
                default:
                    Available = null;
                    Status = $"The update server answered \"{check.Status}\"{(check.Message is { } m ? ": " + m : ".")}";
                    break;
            }
        }
        catch (UpdateVerificationException ex)
        {
            Available = null;
            Status = "Update refused, nothing was changed: " + ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!quiet) Status = "Cannot reach michaeldale.com.au: " + Ui.Describe(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    async Task Install()
    {
        if (Available is not { } update || Busy) return;
        if (!CanInstall)
        {
            await Ui.AlertAsync("Update", "This is a development build (dotnet run), which cannot replace itself. Build or download the packaged app.");
            return;
        }
        if (!await Ui.ConfirmAsync("Install update",
                $"Download and install V9 Retro Controller {update.Version}? The app restarts when it is done."
                + (_services.Backend.IsRemote ? "" : " Background polling pauses for the few seconds the restart takes."),
                "Install and restart")) return;
        Busy = true;
        Progress = 0;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "V9RetroController-update", update.Version);
            using var client = new UpdateClient();
            Status = $"Downloading {update.Package.File}…";
            var zip = await client.DownloadAsync(update, dir, new Progress<double>(p => Progress = p * 100));
            Status = "Installing…";
            await UpdateInstaller.InstallAsync(zip);
            Status = "Restarting…";
            _services.Restart?.Invoke();
        }
        catch (Exception ex)
        {
            Status = "Update failed, nothing was changed: " + Ui.Describe(ex);
        }
        finally
        {
            Busy = false;
        }
    }
}
