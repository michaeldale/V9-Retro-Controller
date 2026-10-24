using V9Retro.Core.Formatting;
using V9Retro.Core.Model;
using V9Retro.Protocol;

namespace V9Retro.Core.Services;

/// <summary>The plugin's status state machine (v9x_poller.php), as pure functions over a copy of
/// the agent row so it can be tested without sockets:
/// <list type="bullet">
/// <item>A successful INFO makes the agent online. A changed boot counter is a restart, a changed
/// agent version an update.</item>
/// <item>"rebooting" completes only when the boot counter moved AND the agent reports our token as
/// its pending job (the v9xctl / MCP proof). It gives up after five minutes.</item>
/// <item>"shutdown" stays until the machine is seen again: a machine we switched off is not an outage.</item>
/// <item>Otherwise an agent goes offline after N consecutive failed polls.</item>
/// </list>
/// Added here: install-ID identity checks.</summary>
public static class StatusRules
{
    public const long RebootGraceMs = 300_000;

    public sealed record Outcome(Agent Agent, IReadOnlyList<(string Type, string Message)> Events, bool Ok, string? Error);

    static bool GraceExpired(Agent a, long now) =>
        a.PowerRequestedAt is null || now - a.PowerRequestedAt.Value > RebootGraceMs;

    static void ClearPower(Agent a)
    {
        a.PowerToken = null;
        a.PowerAction = null;
        a.PowerRequestedAt = null;
    }

    /// <param name="installIdOwner">Id of another saved agent that already owns the reported
    /// install ID, or null.</param>
    public static Outcome Success(Agent before, AgentInfo info, long now, long? installIdOwner = null)
    {
        var a = before.Clone();
        var events = new List<(string, string)>();
        a.LastPollAttempt = now;

        // Identity: a known UUID that changed means a different machine answers at this address.
        var reported = info.InstallId;
        if (reported is not null && ((before.InstallId is not null && before.InstallId != reported)
                                     || (installIdOwner is not null && installIdOwner != before.Id)))
        {
            if (before.IdentityConflict != reported)
                events.Add((AgentEventType.IdentityConflict,
                    installIdOwner is not null && installIdOwner != before.Id
                        ? $"{info.ComputerName} at {before.Address} is already saved as another agent ({reported})"
                        : $"A different machine ({info.ComputerName}, {reported}) answers at {before.Address}"));
            a.IdentityConflict = reported;
            a.LastError = "identity conflict: a different machine answers at this address";
            return new Outcome(a, events, false, a.LastError);
        }
        a.IdentityConflict = null;
        if (reported is not null) a.InstallId = reported;

        var oldBoot = before.BootCounter;
        long newBoot = info.BootCounter;
        var bootChanged = oldBoot is not null && oldBoot != newBoot;

        a.Status = AgentStatus.Online;
        a.LastSeen = now;
        a.LastError = null;
        a.ConsecutiveFailures = 0;
        a.AgentVersion = info.AgentVersion;
        a.BuildId = info.BuildId;
        a.ComputerName = info.ComputerName;
        a.WindowsVersion = info.WindowsVersion;
        a.RawVersion = info.RawWindowsVersion;
        a.WinsockVersion = info.WinsockVersion;
        a.Capabilities = (long)info.Capabilities;
        a.BootCounter = newBoot;
        a.UptimeMs = info.UptimeMs;
        a.DesktopReady = info.Desktop.DesktopReady;
        a.ScreenWidth = (int)info.Desktop.ScreenWidth;
        a.ScreenHeight = (int)info.Desktop.ScreenHeight;
        a.ScreenBpp = (int)info.Desktop.BitsPerPixel;
        a.WindowsDir = info.WindowsDirectory;
        a.SystemDir = info.SystemDirectory;
        a.ListenAddress = info.Desktop.ListenAddress;
        a.AllowedClient = info.Desktop.AllowedClient;
        a.PendingJob = info.Desktop.PendingJob;
        // Keep the inventory-refined OS name; only fall back to INFO's guess.
        if (before.InventoryUpdatedAt is null || string.IsNullOrEmpty(before.OsName))
            a.OsName = V9xFormat.OsName(info.WindowsVersion);

        switch (before.Status)
        {
            case AgentStatus.Rebooting:
                if (bootChanged && info.Desktop.PendingJob == (before.PowerToken ?? ""))
                {
                    events.Add((AgentEventType.Rebooted, $"Back online after {(before.PowerAction == "reset" ? "reset" : "reboot")} (boot {oldBoot} → {newBoot})"));
                    ClearPower(a);
                }
                else if (GraceExpired(before, now))
                {
                    // Answering, but never went down: the reboot did not happen.
                    events.Add((AgentEventType.RebootTimeout, "Agent is answering but did not reboot"));
                    ClearPower(a);
                }
                else
                {
                    a.Status = AgentStatus.Rebooting; // not proven yet: still the pre-reboot agent
                }
                break;
            case AgentStatus.Offline or AgentStatus.Shutdown:
                if (before.Status == AgentStatus.Shutdown) ClearPower(a);
                events.Add((AgentEventType.Online, "Back online" + (bootChanged ? $" (restarted, boot {oldBoot} → {newBoot})" : "")));
                break;
            case AgentStatus.Unknown:
                events.Add((AgentEventType.Online, $"First contact: {info.ComputerName}, agent {info.AgentVersion}"));
                break;
            default:
                if (bootChanged) events.Add((AgentEventType.Restarted, $"Agent restarted (boot {oldBoot} → {newBoot})"));
                break;
        }

        if (!string.IsNullOrEmpty(before.AgentVersion) && before.AgentVersion != info.AgentVersion)
            events.Add((AgentEventType.AgentUpdated, $"Agent {before.AgentVersion} → {info.AgentVersion}"));

        return new Outcome(a, events, true, null);
    }

    public static Outcome Failure(Agent before, string error, long now, int offlineAfter)
    {
        var a = before.Clone();
        var events = new List<(string, string)>();
        a.LastPollAttempt = now;
        a.LastError = error.Length > 255 ? error[..255] : error;
        a.ConsecutiveFailures = before.ConsecutiveFailures + 1;

        switch (before.Status)
        {
            case AgentStatus.Shutdown:
                break;
            case AgentStatus.Rebooting:
                if (GraceExpired(before, now))
                {
                    events.Add((AgentEventType.RebootTimeout, $"Did not come back within {RebootGraceMs / 60000} minutes of the reboot"));
                    a.Status = AgentStatus.Offline;
                    ClearPower(a);
                }
                break;
            default:
                if (before.Status != AgentStatus.Offline && a.ConsecutiveFailures >= Math.Max(1, offlineAfter))
                {
                    a.Status = AgentStatus.Offline;
                    events.Add((AgentEventType.Offline,
                        before.Status == AgentStatus.Unknown ? "Unreachable: " + error : "Went offline: " + error));
                }
                break;
        }
        return new Outcome(a, events, false, error);
    }

    public static bool NeedsInventory(Agent a, int inventoryHours, long now)
    {
        if (!a.Has(Capabilities.Inventory) || a.Status != AgentStatus.Online) return false;
        if (a.InventoryUpdatedAt is null) return true;
        if (a.InventoryBootCounter != a.BootCounter || a.InventoryAgentVersion != a.AgentVersion) return true;
        return now - a.InventoryUpdatedAt.Value > Math.Max(1, inventoryHours) * 3_600_000L;
    }

    public static bool ScreenshotDue(Agent a, int screenshotMinutes, long now) =>
        screenshotMinutes > 0 && a.CanScreenshot
        && (a.ScreenshotAt is null || now - a.ScreenshotAt.Value >= screenshotMinutes * 60_000L - 30_000);

    public static bool PollDue(Agent a, int pollMinutes, long now) =>
        a.Enabled && (a.Status == AgentStatus.Rebooting || a.LastPollAttempt is null
                      || now - a.LastPollAttempt.Value >= Math.Max(1, pollMinutes) * 60_000L - 30_000);
}
