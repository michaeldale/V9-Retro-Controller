using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.App.Services;
using V9Retro.Core.Formatting;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

public sealed record FileRow(FileEntry Entry)
{
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public string Icon => IsDirectory ? "\U0001F4C1" : "\U0001F4C4";
    public string Size => IsDirectory ? "" : V9xFormat.Bytes(Entry.Size);
    public long SizeSort => IsDirectory ? -1 : Entry.Size;
    public string Attributes => $"{(Entry.IsReadOnly ? "R" : "")}{(Entry.IsHidden ? "H" : "")}{(Entry.IsSystem ? "S" : "")}";
}

/// <summary>Browse the guest's drives; upload by picker or drag and drop, download, make folders,
/// delete, hash and read text files (the last three need agent 0.13.0).</summary>
public sealed partial class FilesTab(AgentPageViewModel page) : AgentTabViewModel(page, "Files")
{
    public ObservableCollection<FileRow> Entries { get; } = new();

    [ObservableProperty] public partial string Path { get; set; } = @"C:\";
    [ObservableProperty] public partial FileRow? Selected { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? TransferText { get; set; }

    public bool HasFileTools => Page.Agent.Has(Capabilities.FileTools);
    public bool CanWrite => Page.Agent.Has(Capabilities.FileWrite);

    public override string? Unavailable => Page.Agent.Capabilities != 0 && !Page.Agent.Has(Capabilities.FileRead)
        ? "This agent does not allow file access."
        : null;

    public override void AgentChanged()
    {
        base.AgentChanged();
        OnPropertyChanged(nameof(HasFileTools));
        OnPropertyChanged(nameof(CanWrite));
    }

    protected override Task OnFirstShownAsync() => Refresh();

    static string Join(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    static string Normalise(string path)
    {
        path = path.Trim().Replace('/', '\\');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        return path;
    }

    [RelayCommand]
    Task Refresh() => Run("Listing…", async () =>
    {
        var path = Normalise(Path);
        var list = await UseAsync(s => s.ListAsync(path));
        Path = path;
        Entries.Clear();
        foreach (var e in list.Where(e => e.Name is not ("." or ".."))
                     .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Entries.Add(new FileRow(e));
    });

    [RelayCommand]
    Task Up()
    {
        var path = Normalise(Path).TrimEnd('\\');
        var cut = path.LastIndexOf('\\');
        if (cut < 0) return Task.CompletedTask;
        Path = cut <= 2 ? path[..cut] + "\\" : path[..cut];
        return Refresh();
    }

    [RelayCommand]
    Task Open(FileRow? row)
    {
        row ??= Selected;
        if (row is null) return Task.CompletedTask;
        if (row.IsDirectory)
        {
            Path = Join(Normalise(Path), row.Name);
            return Refresh();
        }
        return HasFileTools ? View(row) : Download(row);
    }

    [RelayCommand]
    async Task Upload()
    {
        if (Ui.Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Upload to {Path}", AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await UploadFiles(paths);
    }

    /// <summary>Uploads local files (from the picker or a drop) into the current folder.</summary>
    public Task UploadFiles(IReadOnlyList<string> localPaths) => Run("Uploading…", async () =>
    {
        var dir = Normalise(Path);
        foreach (var local in localPaths.Where(File.Exists))
        {
            var name = System.IO.Path.GetFileName(local);
            var target = Join(dir, name);
            await using var stream = File.OpenRead(local);
            var total = Math.Max(1, stream.Length);
            TransferText = $"Uploading {name}";
            var progress = new Progress<long>(n => { Progress = 100.0 * n / total; });
            await UseAsync(s => s.UploadAsync(stream, target, progress), TimeSpan.FromSeconds(60));
            if (Page.IsSaved) await Page.Services.Backend.Agents.LogActionAsync(Page.AgentId, $"Uploaded {name} to {target}");
        }
        TransferText = null;
        Progress = 0;
        await Refresh();
    });

    [RelayCommand]
    async Task Download(FileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDirectory || Ui.Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save as", SuggestedFileName = row.Name });
        var local = file?.TryGetLocalPath();
        if (local is null) return;
        var guest = Join(Normalise(Path), row.Name);
        await Run("Downloading…", async () =>
        {
            var temp = local + ".part";
            TransferText = $"Downloading {row.Name}";
            var total = Math.Max(1, row.Entry.Size);
            var progress = new Progress<long>(n => { Progress = 100.0 * n / total; });
            try
            {
                await using (var stream = File.Create(temp))
                    await UseAsync(s => s.DownloadAsync(guest, stream, progress), TimeSpan.FromSeconds(60));
                File.Move(temp, local, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
                TransferText = null;
                Progress = 0;
            }
        });
    }

    [RelayCommand]
    async Task NewFolder()
    {
        var name = await Ui.PromptAsync("New folder", $"Folder name in {Path}:", confirm: "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var target = Join(Normalise(Path), name.Trim());
        await Run("Creating…", async () =>
        {
            await UseAsync(s => s.MkdirAsync(target));
            await Refresh();
        });
    }

    [RelayCommand]
    async Task Delete(FileRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        var target = Join(Normalise(Path), row.Name);
        var recursive = row.IsDirectory;
        if (!await Ui.ConfirmAsync("Delete", recursive
                ? $"Delete the folder {target} and everything in it? A recursive delete cannot be undone."
                : $"Delete {target}?", "Delete", danger: true)) return;
        await Run("Deleting…", async () =>
        {
            var r = await UseAsync(s => s.DeleteAsync(target, recursive, force: true), TimeSpan.FromSeconds(120));
            if (Page.IsSaved) await Page.Services.Backend.Agents.LogActionAsync(Page.AgentId, $"Deleted {target} ({r.FilesDeleted} files, {r.DirectoriesDeleted} folders)");
            await Refresh();
        });
    }

    [RelayCommand]
    Task Hash(FileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDirectory) return Task.CompletedTask;
        var target = Join(Normalise(Path), row.Name);
        return Run("Hashing…", async () =>
        {
            var h = await UseAsync(s => s.HashAsync(target), TimeSpan.FromSeconds(300));
            await Ui.AlertAsync(row.Name, $"{target}\n\nSize: {h.Size:N0} bytes\nCRC32: {h.Crc32:X8}\nSHA-256: {Convert.ToHexString(h.Sha256)}");
        });
    }

    [RelayCommand]
    Task View(FileRow? row)
    {
        row ??= Selected;
        if (row is null || row.IsDirectory) return Task.CompletedTask;
        var target = Join(Normalise(Path), row.Name);
        return Run("Reading…", async () =>
        {
            var text = await UseAsync(s => s.ReadTextAsync(target, tail: 400));
            var note = text.OutputFull ? $" (last {text.LinesReturned} of {text.Lines} lines)" : "";
            await Ui.AlertAsync(row.Name + note, text.Text.Length == 0 ? "(empty)" : text.Text);
        });
    }
}
