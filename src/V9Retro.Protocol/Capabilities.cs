namespace V9Retro.Protocol;

/// <summary>Capability bits advertised in HELLO and INFO (protocol.h V9X_CAP_*).</summary>
[Flags]
public enum Capabilities : uint
{
    None = 0,
    Info = 0x00000001,
    Ping = 0x00000002,
    ExecDirect = 0x00000004,
    ExecShell = 0x00000008,
    ExecCancel = 0x00000010,
    FileRead = 0x00000020,
    FileWrite = 0x00000040,
    PowerControl = 0x00000080,
    ScreenshotBmp = 0x00000100,
    /// <summary>Advertised from 0.5.x, but UPDATE only works from 0.6.0; see <see cref="AgentFeatures"/>.</summary>
    DriverUpdate = 0x00000200,
    InputInject = 0x00000400,
    ExecDetach = 0x00000800,
    HttpDownload = 0x00001000,
    Inventory = 0x00002000,
    /// <summary>INFO accepts a u32 flags payload; flag 1 appends the install ID (0.8.0+).</summary>
    InstallId = 0x00004000,
    InputRelative = 0x00008000,
    HardReset = 0x00010000,
    ScreenshotOptions = 0x00020000,
    FileTools = 0x00040000,
    Process = 0x00080000,
    Registry = 0x00100000,
    Windows = 0x00200000,
}

/// <summary>Feature gates that need more than a capability bit.</summary>
public static class AgentFeatures
{
    static readonly Version UpdateMinimum = new(0, 6, 0);

    public static bool Has(this Capabilities caps, Capabilities wanted) => (caps & wanted) == wanted;

    /// <summary>0.5.x advertises DRIVER_UPDATE but answers UPDATE with UNSUPPORTED_OPERATION.</summary>
    public static bool CanHotUpdate(Capabilities caps, string? agentVersion) =>
        caps.Has(Capabilities.DriverUpdate | Capabilities.FileWrite)
        && ParseVersion(agentVersion) is { } v && v >= UpdateMinimum;

    public static bool CanScreenshot(Capabilities caps) =>
        caps.Has(Capabilities.ScreenshotBmp | Capabilities.FileRead);

    /// <summary>Parses "0.13.0", "0.12.1-dev" and similar; null when unparseable.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) core = core[..cut];
        return Version.TryParse(core, out var v) ? v : null;
    }
}
