namespace V9Retro.Protocol;

/// <summary>The optional tail shared by HELLO and INFO. Older agents stop early, so each
/// part may be missing.</summary>
public sealed record DesktopBlock(
    string PendingJob,
    bool DesktopReady,
    uint ScreenWidth,
    uint ScreenHeight,
    uint BitsPerPixel,
    string ListenAddress,
    string AllowedClient)
{
    public static readonly DesktopBlock Empty = new("", false, 0, 0, 0, "", "");
}

public sealed record HelloInfo(
    ushort ProtocolVersion,
    Capabilities Capabilities,
    uint MaxPayload,
    uint BootCounter,
    ushort Port,
    ushort WinsockVersion,
    string BuildId,
    DesktopBlock Desktop);

public sealed record AgentInfo(
    uint BootCounter,
    uint UptimeMs,
    Capabilities Capabilities,
    ushort Port,
    ushort WinsockVersion,
    uint RawWindowsVersion,
    string AgentVersion,
    string BuildId,
    string ComputerName,
    string WindowsVersion,
    string SystemDirectory,
    string WindowsDirectory,
    string CurrentDirectory,
    DesktopBlock Desktop,
    string? InstallId); // lowercase v4 UUID; null when the agent is older than 0.8.0

public sealed record PingResult(uint UptimeMs, uint BootCounter);

/// <summary>One INVENTORY record. Keys may repeat (for example several net "ip" fields).</summary>
public sealed record InventoryRecord(string Type, IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public string? Get(string key)
    {
        foreach (var f in Fields)
            if (f.Key == key) return f.Value;
        return null;
    }

    public IEnumerable<string> GetAll(string key)
    {
        foreach (var f in Fields)
            if (f.Key == key) yield return f.Value;
    }
}

public enum ExecMode : byte { Direct = 0, Shell = 1 }

public sealed record ExecRequest
{
    public ExecMode Mode { get; init; } = ExecMode.Shell;
    public string Application { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public bool ShowWindow { get; init; }
    public bool Detach { get; init; }
    public uint TimeoutMs { get; init; } = 60_000;
    public uint StdoutLimit { get; init; } = 262_144;
    public uint StderrLimit { get; init; } = 262_144;

    public const uint MaxTimeoutMs = 3_600_000;
    public const uint MaxOutputLimit = 1_048_576;
}

public enum ExecResultCategory : uint { Ok = 0, CreateFailed = 1, Timeout = 2, Cancelled = 3, Internal = 4 }

[Flags]
public enum ExecFlags : uint
{
    None = 0,
    StdoutTruncated = 0x01,
    StderrTruncated = 0x02,
    TimedOut = 0x04,
    Cancelled = 0x08,
    PipeCapture = 0x10,
    GuiWindow = 0x20,
    Detached = 0x40,
    Orphaned = 0x80,
}

public sealed record ExecResult(
    ExecResultCategory Result,
    int ExitCode,
    uint NativeError,
    uint ElapsedMs,
    uint StdoutBytes,
    uint StderrBytes,
    ExecFlags Flags);

/// <summary>Events streamed by an EXEC request, in order: output, then exactly one Completed.</summary>
public abstract record ExecEvent
{
    public sealed record Accepted(uint RequestId) : ExecEvent;
    public sealed record Stdout(byte[] Data) : ExecEvent;
    public sealed record Stderr(byte[] Data) : ExecEvent;
    public sealed record Completed(ExecResult Result) : ExecEvent;
}

public sealed record FileStat(bool Exists, bool IsDirectory, uint Size, uint Attributes, uint NativeError);

public sealed record FileEntry(string Name, uint Size, uint Attributes)
{
    public bool IsDirectory => (Attributes & 0x10) != 0;
    public bool IsReadOnly => (Attributes & 0x01) != 0;
    public bool IsHidden => (Attributes & 0x02) != 0;
    public bool IsSystem => (Attributes & 0x04) != 0;
}

public sealed record FileDeleteResult(uint FilesDeleted, uint DirectoriesDeleted);

public sealed record FileHashResult(uint Size, uint Crc32, byte[] Sha256);

public sealed record ReadTextResult(
    uint FileSize, uint Lines, uint MatchingLines, uint LinesReturned,
    bool OutputFull, bool LongLinesCut, string Text);

public sealed record TransferResult(long Size, uint Crc32);

public sealed record DownloadResult(uint HttpStatus, uint Size, uint Crc32);

public sealed record PowerAccepted(uint BootCounter, string Token);

public sealed record UpdateAcceptedResult(uint BootCounter, uint AgentSize);

[Flags]
public enum ScreenshotFlags : uint
{
    None = 0,
    /// <summary>Lossless 8-bit with an exact palette when there are at most 256 colours.</summary>
    Compact = 0x1,
    /// <summary>8-bit greyscale (lossy).</summary>
    Gray = 0x2,
}

/// <summary>Agent-side shaping (0.13.0+, <see cref="Capabilities.ScreenshotOptions"/>).
/// A zero width or height means the whole screen; scale 0 means 100.</summary>
public sealed record ScreenshotOptions(
    ScreenshotFlags Flags = ScreenshotFlags.Compact,
    uint X = 0, uint Y = 0, uint Width = 0, uint Height = 0,
    uint ScalePercent = 100);

public sealed record ScreenshotInfo(uint Width, uint Height, uint SourceBitsPerPixel, uint FileSize, uint Crc32, string GuestPath);

public sealed record ProcessEntry(uint Pid, uint ParentPid, uint Threads, uint Flags, uint ExecRequestId, string Image)
{
    public bool IsAgentJob => (Flags & 0x1) != 0;
    public bool IsAgent => (Flags & 0x2) != 0;
    public string Name
    {
        get
        {
            var cut = Image.LastIndexOfAny(['\\', '/', ':']);
            return cut >= 0 ? Image[(cut + 1)..] : Image;
        }
    }
}

public sealed record ProcessKillResult(uint Pid, uint ExecRequestId);

public sealed record WindowEntry(uint Handle, uint Pid, uint Flags, int Left, int Top, int Right, int Bottom, string ClassName, string Title)
{
    public bool IsForeground => (Flags & 0x1) != 0;
    public bool IsMinimized => (Flags & 0x2) != 0;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record WindowListResult(IReadOnlyList<WindowEntry> Windows, bool Truncated);

public enum RegistryHive : uint
{
    ClassesRoot = 0x80000000,
    CurrentUser = 0x80000001,
    LocalMachine = 0x80000002,
    Users = 0x80000003,
    CurrentConfig = 0x80000005,
    DynData = 0x80000006,
}

public enum RegistryValueType : uint
{
    None = 0, String = 1, ExpandString = 2, Binary = 3, DWord = 4, DWordBigEndian = 5,
    Link = 6, MultiString = 7, ResourceList = 8,
}

public sealed record RegistryValue(string Name, RegistryValueType Type, byte[] Data)
{
    /// <summary>The data as display text: strings decoded, DWORDs as hex and decimal, others as hex bytes.</summary>
    public string DisplayData => Type switch
    {
        RegistryValueType.String or RegistryValueType.ExpandString =>
            Wire.GuestText.Decode(Data).TrimEnd('\0'),
        RegistryValueType.MultiString =>
            string.Join(" | ", Wire.GuestText.Decode(Data).Split('\0', StringSplitOptions.RemoveEmptyEntries)),
        RegistryValueType.DWord when Data.Length >= 4 =>
            $"0x{BitConverter.ToUInt32(Data, 0):X8} ({BitConverter.ToUInt32(Data, 0)})",
        _ => Convert.ToHexString(Data),
    };
}

public sealed record RegistryQueryResult(IReadOnlyList<string> Subkeys, IReadOnlyList<RegistryValue> Values, bool Truncated);

public sealed record InputResult(uint ActionsPerformed, int CursorX, int CursorY);
