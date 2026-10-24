using System.Diagnostics;
using System.IO.Compression;

namespace V9Retro.App.Services;

/// <summary>Puts a verified update package in place of the running app.
/// <list type="bullet">
/// <item>Windows: the package zip holds V9RetroController.exe. The running exe is renamed to
/// .old (Windows allows renaming a running image), the new one moved in, and the app restarts.</item>
/// <item>macOS: the zip holds "V9 Retro Controller.app" (made with ditto). The running bundle is
/// renamed to .old, the new bundle moved in, and the app restarts.</item>
/// </list>
/// The .old copy is deleted at the next start. A development build (dotnet run) cannot update itself.</summary>
static class UpdateInstaller
{
    const string ExeName = "V9RetroController.exe";

    /// <summary>The running exe (Windows) or .app bundle (macOS) when this is a packaged build.</summary>
    public static string? Target
    {
        get
        {
            var process = Environment.ProcessPath;
            if (process is null) return null;
            if (OperatingSystem.IsWindows())
            {
                // The single-file exe has no V9RetroController.dll beside it; a dev build does.
                return Path.GetFileName(process).Equals(ExeName, StringComparison.OrdinalIgnoreCase)
                       && !File.Exists(Path.Combine(AppContext.BaseDirectory, "V9RetroController.dll"))
                    ? process
                    : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                var marker = process.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
                return marker > 0 ? process[..(marker + 4)] : null;
            }
            return null;
        }
    }

    public static bool CanInstall => Target is not null;

    /// <summary>Removes what a previous update left behind.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Target is not { } target) return;
            var old = target + ".old";
            if (File.Exists(old)) File.Delete(old);
            else if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        }
        catch (Exception)
        {
            // Still locked (the old process is exiting); next start tries again.
        }
    }

    /// <summary>Unpacks <paramref name="zipPath"/> and swaps it in. The caller restarts the app.</summary>
    public static async Task InstallAsync(string zipPath)
    {
        var target = Target ?? throw new InvalidOperationException("this is a development build; run the packaged app to update");
        var staging = Path.Combine(Path.GetDirectoryName(zipPath)!, "unpacked");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        if (OperatingSystem.IsWindows())
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            var fresh = Path.Combine(staging, ExeName);
            if (!File.Exists(fresh)) throw new InvalidOperationException($"the package has no {ExeName}");
            var old = target + ".old";
            if (File.Exists(old)) File.Delete(old);
            try
            {
                File.Move(target, old);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"cannot replace {target} (no write access to its folder). The verified package is in {zipPath}.");
            }
            try
            {
                File.Move(fresh, target);
            }
            catch
            {
                File.Move(old, target); // put the running version back
                throw;
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // ditto keeps permissions, symlinks and the bundle's signature intact.
            await RunAsync("/usr/bin/ditto", "-x", "-k", zipPath, staging);
            var bundle = Directory.GetDirectories(staging, "*.app").FirstOrDefault()
                         ?? throw new InvalidOperationException("the package has no .app bundle");
            var old = target + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            Directory.Move(target, old);
            try
            {
                Directory.Move(bundle, target);
            }
            catch
            {
                Directory.Move(old, target);
                throw;
            }
            return;
        }

        throw new PlatformNotSupportedException();
    }

    static async Task RunAsync(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} failed: {error.Trim()}");
    }
}
