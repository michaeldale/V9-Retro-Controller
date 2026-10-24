using System.Text;
using V9Retro.Core.Model;
using V9Retro.Core.Updates;
using V9Retro.Protocol;

namespace V9Retro.Core.Services;

public sealed record VncStatus(bool Installed, bool Enabled, bool Running, int Port, bool PasswordSet, bool SetupAvailable);

public sealed record AgentUpdateResult(string PreviousBuild, uint PreviousBoot, string Build, uint Boot, bool VncUpdated, bool SetupUpdated)
{
    /// <summary>Set when nothing was installed (already current).</summary>
    public string? Message { get; init; }
}

/// <summary>VNC control and agent updates, built from file and exec requests as v9xctl and the MCP
/// server do (the protocol has no VNC message).
/// <para>Unlike v9xctl, this can also turn VNC on: the owner chose to allow it from the desktop app
/// behind a confirmation, which departs from decisions/2026-10-15-vnc-remote-control.md (there,
/// only SETUP.EXE on the guest enables it). Enabling runs the installed SETUP.EXE /S /vnc, so the
/// guest gets exactly what its own checkbox does (enabled=1, autostart, started now).</para></summary>
public sealed class MaintenanceService(IAgentService agents)
{
    public const string AgentDir = @"C:\V9XREMOTE";
    public const string AgentIni = @"C:\V9XREMOTE\AGENT.INI";
    public const string VncExe = @"C:\V9XREMOTE\V9XVNC.EXE";
    public const string SetupExe = @"C:\V9XREMOTE\SETUP.EXE";

    IAgentConnector Connector => agents.Connector;

    Task LogAsync(Agent a, string message) => a.IsSaved ? agents.LogActionAsync(a.Id, message) : Task.CompletedTask;

    // ---- INI helpers -------------------------------------------------------------------

    internal static (bool Enabled, int Port, bool PasswordSet) ParseVncIni(string text)
    {
        bool enabled = false, passwordSet = false;
        var port = 5900;
        foreach (var (section, key, value) in IniEntries(text))
        {
            if (section != "vnc") continue;
            if (key == "enabled") enabled = value == "1";
            else if (key == "port" && int.TryParse(value, out var p)) port = p;
            else if (key == "password") passwordSet = value.Length > 0;
        }
        return (enabled, port, passwordSet);
    }

    static IEnumerable<(string Section, string Key, string Value)> IniEntries(string text)
    {
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim().ToLowerInvariant(); continue; }
            if (line.StartsWith(';') || !line.Contains('=')) continue;
            var eq = line.IndexOf('=');
            yield return (section, line[..eq].Trim().ToLowerInvariant(), line[(eq + 1)..].Trim());
        }
    }

    /// <summary>Sets or adds <c>key=value</c> in <c>[section]</c>, keeping everything else and CRLF
    /// line ends (the guest reads the file with the profile API).</summary>
    internal static string SetIniValue(string text, string section, string key, string value)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        int sectionAt = -1, insertAt = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (sectionAt >= 0 && insertAt < 0) insertAt = i;
                if (string.Equals(t[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase)) sectionAt = i;
                continue;
            }
            if (sectionAt >= 0 && insertAt < 0 && t.Contains('=') && !t.StartsWith(';')
                && string.Equals(t[..t.IndexOf('=')].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join("\r\n", lines) + "\r\n";
            }
        }
        if (sectionAt < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            var at = insertAt < 0 ? lines.Count : insertAt;
            while (at > sectionAt + 1 && lines[at - 1].Trim().Length == 0) at--;
            lines.Insert(at, $"{key}={value}");
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    // ---- VNC ---------------------------------------------------------------------------

    static async Task<bool?> ProbeVncAsync(V9xSession s, CancellationToken ct)
    {
        // Exit 3 ("already running") comes from builds before -status.
        var (result, _, _) = await s.ExecAsync(new ExecRequest
        {
            Mode = ExecMode.Direct, Application = VncExe, Arguments = "-status", TimeoutMs = 15000,
        }, ct).ConfigureAwait(false);
        if (result.Result != ExecResultCategory.Ok) return null;
        return result.ExitCode is 0 or 3;
    }

    static async Task<bool> WaitVncAsync(V9xSession s, bool running, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await ProbeVncAsync(s, ct).ConfigureAwait(false) == running) return true;
        }
        return false;
    }

    static async Task<VncStatus> StatusCoreAsync(V9xSession s, CancellationToken ct)
    {
        bool enabled = false, passwordSet = false;
        var port = 5900;
        try
        {
            var ini = Encoding.ASCII.GetString(await s.DownloadBytesAsync(AgentIni, ct).ConfigureAwait(false));
            (enabled, port, passwordSet) = ParseVncIni(ini);
        }
        catch (V9xGuestException)
        {
            // No AGENT.INI readable: report defaults.
        }
        var setup = (await s.StatAsync(SetupExe, ct).ConfigureAwait(false)).Exists;
        var running = await ProbeVncAsync(s, ct).ConfigureAwait(false);
        return new VncStatus(running is not null, enabled, running == true, port, passwordSet, setup);
    }

    public Task<VncStatus> VncStatusAsync(Agent a, CancellationToken ct = default) =>
        Connector.UseAsync(a.Host, a.Port, s => StatusCoreAsync(s, ct), ct, TimeSpan.FromSeconds(30));

    /// <summary>Starts V9XVNC.EXE when it is enabled ([vnc] enabled=1), as v9xctl vnc start does.</summary>
    public Task<VncStatus> StartVncAsync(Agent a, CancellationToken ct = default) =>
        Connector.UseAsync(a.Host, a.Port, async s =>
        {
            var status = await StatusCoreAsync(s, ct).ConfigureAwait(false);
            if (!status.Installed) throw new V9xException($"{VncExe} is not installed (agent package 0.11.0 or later)");
            if (status.Running) return status;
            if (!status.Enabled) throw new V9xException("VNC is disabled on this machine ([vnc] enabled=0); enable it first");
            await s.ExecAsync(new ExecRequest { Mode = ExecMode.Direct, Application = VncExe, Detach = true }, ct).ConfigureAwait(false);
            if (!await WaitVncAsync(s, true, ct).ConfigureAwait(false))
                throw new V9xException(@"V9XVNC.EXE did not start; see C:\V9XREMOTE\VNC.LOG on the guest");
            await LogAsync(a, "Started VNC server");
            return status with { Running = true };
        }, ct, TimeSpan.FromSeconds(30));

    public Task<VncStatus> StopVncAsync(Agent a, CancellationToken ct = default) =>
        Connector.UseAsync(a.Host, a.Port, async s =>
        {
            var status = await StatusCoreAsync(s, ct).ConfigureAwait(false);
            if (!status.Running) return status;
            await s.ExecAsync(new ExecRequest { Mode = ExecMode.Direct, Application = VncExe, Arguments = "-stop", TimeoutMs = 15000 }, ct)
                .ConfigureAwait(false);
            if (!await WaitVncAsync(s, false, ct).ConfigureAwait(false))
                throw new V9xException("V9XVNC.EXE did not stop within 10 s");
            await LogAsync(a, "Stopped VNC server");
            return status with { Running = false };
        }, ct, TimeSpan.FromSeconds(30));

    /// <summary>Turns VNC on or off on the guest, persistently. With SETUP.EXE (0.11.0+ packages)
    /// this runs <c>SETUP.EXE /S /vnc</c> or <c>/novnc</c>; otherwise it edits AGENT.INI and starts
    /// or stops the server (on Windows 9x that one does not autostart until SETUP.EXE is run).
    /// A password (at most 8 characters, stored in plain text in AGENT.INI by the agent's design)
    /// is written when given, and the server restarted so it takes effect. The INI is read back
    /// afterwards because Windows 9x caches INI writes.</summary>
    public Task<VncStatus> SetVncEnabledAsync(Agent a, bool enable, string? newPassword = null, CancellationToken ct = default) =>
        Connector.UseAsync(a.Host, a.Port, async s =>
        {
            if (newPassword is { Length: > 8 }) throw new ArgumentException("VNC passwords are at most 8 characters");
            var status = await StatusCoreAsync(s, ct).ConfigureAwait(false);
            if (!status.Installed) throw new V9xException($"{VncExe} is not installed (agent package 0.11.0 or later)");

            if (status.SetupAvailable)
            {
                var (result, _, stderr) = await s.ExecAsync(new ExecRequest
                {
                    Mode = ExecMode.Direct, Application = SetupExe, Arguments = enable ? "/S /vnc" : "/S /novnc",
                    WorkingDirectory = AgentDir, TimeoutMs = 60_000,
                }, ct).ConfigureAwait(false);
                if (result.Result != ExecResultCategory.Ok || result.ExitCode != 0)
                    throw new V9xException($"SETUP.EXE {(enable ? "/vnc" : "/novnc")} failed (exit {result.ExitCode}) {stderr}".Trim());
            }
            else
            {
                await WriteIniValueAsync(s, "enabled", enable ? "1" : "0", ct).ConfigureAwait(false);
            }

            if (enable && newPassword is not null)
                await WriteIniValueAsync(s, "password", newPassword, ct).ConfigureAwait(false);

            // Restart (password change, or no SETUP.EXE to start it) / stop to match the request.
            var running = await ProbeVncAsync(s, ct).ConfigureAwait(false) == true;
            if (running && (!enable || newPassword is not null))
            {
                await s.ExecAsync(new ExecRequest { Mode = ExecMode.Direct, Application = VncExe, Arguments = "-stop", TimeoutMs = 15000 }, ct)
                    .ConfigureAwait(false);
                await WaitVncAsync(s, false, ct).ConfigureAwait(false);
                running = false;
            }
            if (enable && !running)
            {
                await s.ExecAsync(new ExecRequest { Mode = ExecMode.Direct, Application = VncExe, Detach = true }, ct).ConfigureAwait(false);
                await WaitVncAsync(s, true, ct).ConfigureAwait(false);
            }

            var after = await StatusCoreAsync(s, ct).ConfigureAwait(false);
            if (after.Enabled != enable)
                throw new V9xException($"AGENT.INI still says enabled={(after.Enabled ? 1 : 0)} after the change");
            if (enable && newPassword is not null && after.PasswordSet != (newPassword.Length > 0))
                throw new V9xException("the VNC password did not reach AGENT.INI");
            await LogAsync(a, enable ? "Enabled VNC remote control" + (newPassword is { Length: > 0 } ? " with a password" : "")
                : "Disabled VNC remote control");
            return after;
        }, ct, TimeSpan.FromSeconds(90));

    static async Task WriteIniValueAsync(V9xSession s, string key, string value, CancellationToken ct)
    {
        // STAT first: on Windows 9x it flushes the profile cache for this file (agent 0.13.0+).
        await s.StatAsync(AgentIni, ct).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(await s.DownloadBytesAsync(AgentIni, ct).ConfigureAwait(false));
        var updated = SetIniValue(text, "vnc", key, value);
        await s.UploadBytesAsync(Encoding.ASCII.GetBytes(updated), AgentIni, ct).ConfigureAwait(false);
    }

    // ---- agent update ------------------------------------------------------------------

    /// <summary>Hot-updates the agent from an install package folder (V9XAGNT.EXE and V9XSHOT.EXE,
    /// optionally V9XVNC.EXE and SETUP.EXE), then waits for it to come back with a new boot counter.</summary>
    public async Task<AgentUpdateResult> UpdateAgentAsync(Agent a, string packageDir, IProgress<string>? progress = null,
        TimeSpan? wait = null, CancellationToken ct = default)
    {
        var agentPath = Path.Combine(packageDir, "V9XAGNT.EXE");
        var helperPath = Path.Combine(packageDir, "V9XSHOT.EXE");
        if (!File.Exists(agentPath) || !File.Exists(helperPath))
            throw new FileNotFoundException("the package must contain V9XAGNT.EXE and V9XSHOT.EXE", packageDir);
        var agentData = await File.ReadAllBytesAsync(agentPath, ct).ConfigureAwait(false);
        var helperData = await File.ReadAllBytesAsync(helperPath, ct).ConfigureAwait(false);

        var (oldBuild, oldBoot, vncUpdated, setupUpdated) = await Connector.UseAsync(a.Host, a.Port, async s =>
        {
            var info = await s.InfoAsync(ct).ConfigureAwait(false);
            if (!AgentFeatures.CanHotUpdate(s.Capabilities, info.AgentVersion))
                throw new V9xUnsupportedException(
                    $"agent {info.AgentVersion} predates hot update (0.6.0); upgrade it once with v9xctl update, which stages and reboots");
            var vncDone = false;
            var vncPath = Path.Combine(packageDir, "V9XVNC.EXE");
            if (File.Exists(vncPath))
            {
                progress?.Report("Replacing V9XVNC.EXE");
                try
                {
                    var wasRunning = await ProbeVncAsync(s, ct).ConfigureAwait(false) == true;
                    if (wasRunning)
                    {
                        await s.ExecAsync(new ExecRequest { Mode = ExecMode.Direct, Application = VncExe, Arguments = "-stop", TimeoutMs = 15000 }, ct)
                            .ConfigureAwait(false);
                        await WaitVncAsync(s, false, ct).ConfigureAwait(false);
                    }
                    await s.UploadBytesAsync(await File.ReadAllBytesAsync(vncPath, ct).ConfigureAwait(false), VncExe, ct).ConfigureAwait(false);
                    if (wasRunning)
                        await s.ExecAsync(new ExecRequest { Mode = ExecMode.Direct, Application = VncExe, Detach = true }, ct).ConfigureAwait(false);
                    vncDone = true;
                }
                catch (V9xGuestException)
                {
                    // The agent update goes ahead; v9xctl warns likewise.
                }
            }
            var setupDone = false;
            var setupPath = Path.Combine(packageDir, "SETUP.EXE");
            if (File.Exists(setupPath))
            {
                progress?.Report("Replacing SETUP.EXE");
                try
                {
                    await s.UploadBytesAsync(await File.ReadAllBytesAsync(setupPath, ct).ConfigureAwait(false), SetupExe, ct).ConfigureAwait(false);
                    setupDone = true;
                }
                catch (V9xGuestException)
                {
                    // Open in the guest; the agent update goes ahead.
                }
            }
            progress?.Report("Staging V9XNEW.EXE and V9XSNEW.EXE");
            await s.UploadBytesAsync(agentData, AgentDir + @"\V9XNEW.EXE", ct).ConfigureAwait(false);
            await s.UploadBytesAsync(helperData, AgentDir + @"\V9XSNEW.EXE", ct).ConfigureAwait(false);
            progress?.Report("Applying update");
            await s.ApplyUpdateAsync((uint)agentData.Length, Crc32.Compute(agentData), (uint)helperData.Length, Crc32.Compute(helperData), ct)
                .ConfigureAwait(false);
            return (s.Hello.BuildId, s.Hello.BootCounter, vncDone, setupDone);
        }, ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);

        progress?.Report("Waiting for the agent to restart");
        var hello = await WaitForRestartAsync(a, oldBoot, wait, ct).ConfigureAwait(false);
        await LogAsync(a, $"Agent updated: {oldBuild} → {hello.BuildId}");
        if (a.IsSaved) await agents.PollAsync(a.Id, ct: ct).ConfigureAwait(false);
        return new AgentUpdateResult(oldBuild, oldBoot, hello.BuildId, hello.BootCounter, vncUpdated, setupUpdated);
    }

    /// <summary>Waits for the agent to answer with a boot counter other than <paramref name="oldBoot"/>
    /// (the hot swap restarted it).</summary>
    async Task<HelloInfo> WaitForRestartAsync(Agent a, uint oldBoot, TimeSpan? wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(120));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            try
            {
                var hello = await Connector.UseAsync(a.Host, a.Port, s => Task.FromResult(s.Hello), ct).ConfigureAwait(false);
                if (hello.BootCounter != oldBoot) return hello;
            }
            catch (V9xException)
            {
                // Still swapping.
            }
        }
        throw new V9xException("the agent did not come back with a new boot counter in time; see C:\\V9XREMOTE\\HOTSWAP.LOG");
    }

    // ---- agent update from the update server -------------------------------------------

    /// <summary>The agent version that can update itself unattended (SETUP.EXE /update /S).</summary>
    public static readonly Version GuestSelfUpdateMinimum = new(0, 13, 1);

    public const string UpdateResultIni = @"C:\V9XREMOTE\TEMP\UPDATE.INI";

    /// <summary>Whether the agent can fetch and stage its own update (agent 0.13.1+, Windows, EXEC).</summary>
    public static bool CanSelfUpdate(Agent a) =>
        AgentFeatures.ParseVersion(a.AgentVersion) is { } v && v >= GuestSelfUpdateMinimum
        && a.Has(Capabilities.ExecDirect) && !(a.WindowsVersion ?? "").StartsWith("DOS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks michaeldale.com.au about this agent (app v9x-remote-agent, family win32) and,
    /// when it offers an update, verifies the signed release under the V9x release key.
    /// The check's status is current, no_release and so on when there is nothing to do.</summary>
    public static async Task<(UpdateCheck Check, VerifiedUpdate? Update)> CheckAgentUpdateAsync(Agent a, CancellationToken ct = default)
    {
        var installed = AgentFeatures.ParseVersion(a.AgentVersion) is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}"
            : throw new V9xException("poll the agent first: its version is not known");
        using var client = AgentUpdateClient();
        var check = await client.CheckAsync(installed, UpdateClient.AgentFamily, ct).ConfigureAwait(false);
        if (!check.UpdateAvailable) return (check, null);
        return (check, await client.VerifyAsync(check, installed, UpdateClient.AgentFamily, ct).ConfigureAwait(false));
    }

    static UpdateClient AgentUpdateClient() =>
        new(app: UpdateClient.AgentApp, releaseSection: UpdateClient.AgentReleaseSection);

    /// <summary>The agent updates itself from the server: SETUP.EXE /update /S downloads, verifies and
    /// stages the release on the guest (the PC sends no binaries), then this applies the staged
    /// V9XNEW.EXE / V9XSNEW.EXE with a hot swap so it takes over without a reboot.</summary>
    public async Task<AgentUpdateResult> UpdateAgentFromServerOnGuestAsync(Agent a, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!CanSelfUpdate(a))
            throw new V9xUnsupportedException(
                $"agent {a.AgentVersion} cannot update itself unattended (needs {GuestSelfUpdateMinimum}); update it through this PC");

        progress?.Report("The agent is checking michaeldale.com.au and downloading the update itself…");
        var (oldBuild, oldBoot, exec, ini) = await Connector.UseAsync(a.Host, a.Port, async s =>
        {
            var (result, _, _) = await s.ExecAsync(new ExecRequest
            {
                Mode = ExecMode.Direct, Application = SetupExe, Arguments = "/update /S", WorkingDirectory = AgentDir,
                TimeoutMs = 20 * 60_000,
            }, ct).ConfigureAwait(false);
            var text = "";
            try { text = Encoding.ASCII.GetString(await s.DownloadBytesAsync(UpdateResultIni, ct).ConfigureAwait(false)); }
            catch (V9xGuestException) { /* no result file: report the exit code */ }
            return (s.Hello.BuildId, s.Hello.BootCounter, result, ParseResultIni(text));
        }, ct, TimeSpan.FromMinutes(21)).ConfigureAwait(false);

        string Value(string key) => ini.GetValueOrDefault(key) ?? "";
        if (exec.Result != ExecResultCategory.Ok)
            throw new V9xException($"SETUP.EXE /update /S did not finish ({exec.Result})");
        if (exec.ExitCode == 3)
            return new AgentUpdateResult(oldBuild, oldBoot, oldBuild, oldBoot, false, false)
            {
                Message = Value("message") is { Length: > 0 } m ? m : "Already the newest version.",
            };
        if (exec.ExitCode != 0)
            throw new V9xException("The agent's update failed: " + (Value("message") is { Length: > 0 } msg ? msg : $"exit code {exec.ExitCode}"));
        await LogAsync(a, $"Agent downloaded and staged {Value("latest")} from the update server");

        progress?.Report($"Staged {Value("latest")}; applying it with a hot swap…");
        await Connector.UseAsync(a.Host, a.Port, async s =>
        {
            var agentFile = await StagedAsync(s, AgentDir + @"\V9XNEW.EXE", ct).ConfigureAwait(false);
            var helperFile = await StagedAsync(s, AgentDir + @"\V9XSNEW.EXE", ct).ConfigureAwait(false);
            await s.ApplyUpdateAsync(agentFile.Size, agentFile.Crc32, helperFile.Size, helperFile.Crc32, ct).ConfigureAwait(false);
            return true;
        }, ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);

        progress?.Report("Waiting for the agent to restart");
        var hello = await WaitForRestartAsync(a, oldBoot, null, ct).ConfigureAwait(false);
        await LogAsync(a, $"Agent updated from the update server: {oldBuild} → {hello.BuildId}");
        if (a.IsSaved) await agents.PollAsync(a.Id, ct: ct).ConfigureAwait(false);
        return new AgentUpdateResult(oldBuild, oldBoot, hello.BuildId, hello.BootCounter, false, false);
    }

    /// <summary>Size and CRC32 of a staged file, hashed on the guest when it can (0.13.0+).</summary>
    static async Task<(uint Size, uint Crc32)> StagedAsync(V9xSession s, string path, CancellationToken ct)
    {
        if (s.Capabilities.Has(Capabilities.FileTools))
        {
            var h = await s.HashAsync(path, ct).ConfigureAwait(false);
            return (h.Size, h.Crc32);
        }
        var bytes = await s.DownloadBytesAsync(path, ct).ConfigureAwait(false);
        return ((uint)bytes.Length, Crc32.Compute(bytes));
    }

    /// <summary>The PC fetches the verified package from the server and pushes it with the usual hot
    /// update (upload, then UPDATE_REQUEST). Works for every agent from 0.6.0.</summary>
    public async Task<AgentUpdateResult> UpdateAgentFromServerThroughHostAsync(Agent a, VerifiedUpdate update,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "V9RetroController-agent-update", update.Version);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        progress?.Report($"Downloading {update.Package.File} from michaeldale.com.au…");
        using var client = AgentUpdateClient();
        var zip = await client.DownloadAsync(update, dir, null, ct).ConfigureAwait(false);
        var package = Path.Combine(dir, "package");
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, package);
        return await UpdateAgentAsync(a, package, progress, null, ct).ConfigureAwait(false);
    }

    static Dictionary<string, string> ParseResultIni(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, key, value) in IniEntries(text))
            if (section == "result") values.TryAdd(key, value);
        return values;
    }
}
