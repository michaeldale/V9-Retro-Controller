using System.Text.Json.Serialization;
using V9Retro.Protocol;

namespace V9Retro.Core.Model;

public static class AgentStatus
{
    public const string Unknown = "unknown";
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Rebooting = "rebooting";
    public const string Shutdown = "shutdown";
}

/// <summary>One row of <c>agents</c>. Mutable so the poller can build the next state from a copy.
/// Column names map with underscores (Dapper MatchNamesWithUnderscores).</summary>
public sealed class Agent
{
    // Settings the user edits.
    public long Id { get; set; }
    public string? InstallId { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9869;
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
    public int VncPort { get; set; } = 5900;
    public string? VncPassword { get; set; }
    public long DateAdded { get; set; }
    public long LastModified { get; set; }

    // Poll state.
    public string Status { get; set; } = AgentStatus.Unknown;
    public long? LastPollAttempt { get; set; }
    public long? LastSeen { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? IdentityConflict { get; set; }

    // From INFO.
    public string? AgentVersion { get; set; }
    public string? BuildId { get; set; }
    public string? ComputerName { get; set; }
    public string? WindowsVersion { get; set; }
    public string? OsName { get; set; }
    public long? RawVersion { get; set; }
    public int? WinsockVersion { get; set; }
    public long Capabilities { get; set; }
    public long? BootCounter { get; set; }
    public long? UptimeMs { get; set; }
    public bool DesktopReady { get; set; }
    public int? ScreenWidth { get; set; }
    public int? ScreenHeight { get; set; }
    public int? ScreenBpp { get; set; }
    public string? WindowsDir { get; set; }
    public string? SystemDir { get; set; }
    public string? ListenAddress { get; set; }
    public string? AllowedClient { get; set; }
    public string? PendingJob { get; set; }

    // Summary from INVENTORY.
    public string? CpuSummary { get; set; }
    public long? RamTotal { get; set; }
    public long? DiskTotal { get; set; }
    public long? DiskFree { get; set; }
    public long? InventoryUpdatedAt { get; set; }
    public string? InventoryAgentVersion { get; set; }
    public long? InventoryBootCounter { get; set; }
    public string? InventoryHash { get; set; }

    public long? LastScreenshotId { get; set; }
    public long? ScreenshotAt { get; set; }

    public string? PowerToken { get; set; }
    public string? PowerAction { get; set; }
    public long? PowerRequestedAt { get; set; }

    [JsonIgnore] public Capabilities Caps => (Capabilities)(uint)Capabilities;
    public bool Has(Capabilities c) => Caps.Has(c);
    [JsonIgnore] public bool IsOnline => Status == AgentStatus.Online;
    [JsonIgnore] public bool IsSaved => Id > 0;
    [JsonIgnore] public string Address => $"{Host}:{Port}";

    [JsonIgnore] public bool CanScreenshot => IsOnline && DesktopReady && AgentFeatures.CanScreenshot(Caps);
    [JsonIgnore] public bool CanHotUpdate => IsOnline && AgentFeatures.CanHotUpdate(Caps, AgentVersion);

    public Agent Clone() => (Agent)MemberwiseClone();
}

public sealed record AgentEvent(long Id, long AgentId, long DateAdded, string Type, string Message);

public static class AgentEventType
{
    public const string Added = "added";
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Restarted = "restarted";
    public const string Rebooted = "rebooted";
    public const string RebootRequested = "reboot_requested";
    public const string RebootTimeout = "reboot_timeout";
    public const string ShutdownRequested = "shutdown_requested";
    public const string ResetRequested = "reset_requested";
    public const string AgentUpdated = "agent_updated";
    public const string InventoryChanged = "inventory_changed";
    public const string AddressChanged = "address_changed";
    public const string IdentityConflict = "identity_conflict";
    public const string Action = "action";

    public static string Label(string type) => type switch
    {
        Added => "Added",
        Online => "Online",
        Offline => "Offline",
        Restarted => "Restarted",
        Rebooted => "Rebooted",
        RebootRequested => "Reboot requested",
        RebootTimeout => "Reboot timed out",
        ShutdownRequested => "Shutdown requested",
        ResetRequested => "Reset requested",
        AgentUpdated => "Agent updated",
        InventoryChanged => "Hardware changed",
        AddressChanged => "Address changed",
        IdentityConflict => "Identity conflict",
        _ => "Action",
    };
}

public sealed record ScreenshotRecord(long Id, long AgentId, long DateAdded, long Width, long Height, long Bpp,
    string Source, string FileName);

public sealed record InventoryRow(string RecordType, long RecordIndex, string FieldsJson);
