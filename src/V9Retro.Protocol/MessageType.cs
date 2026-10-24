namespace V9Retro.Protocol;

/// <summary>Message types from include\v9xremote\protocol.h. Requests are 0x0xxx,
/// responses 0x8xxx and streamed data 0x9xxx.</summary>
public static class MessageType
{
    public const ushort HelloRequest = 0x0001;
    public const ushort HelloResponse = 0x8001;
    public const ushort PingRequest = 0x0002;
    public const ushort PingResponse = 0x8002;

    public const ushort ExecRequest = 0x0010;
    public const ushort ExecAccepted = 0x8010;
    public const ushort ExecStdout = 0x9010;
    public const ushort ExecStderr = 0x9011;
    public const ushort ExecComplete = 0x8011;
    public const ushort CancelRequest = 0x0011;
    public const ushort CancelResponse = 0x8012;

    public const ushort FileStatRequest = 0x0020;
    public const ushort FileStatResponse = 0x8020;
    public const ushort FileListRequest = 0x0021;
    public const ushort FileListResponse = 0x8021;
    public const ushort FileMkdirRequest = 0x0022;
    public const ushort FileMkdirResponse = 0x8022;
    public const ushort FileOpenWrite = 0x0023;
    public const ushort FileWriteReady = 0x8023;
    public const ushort FileWriteChunk = 0x0024;
    public const ushort FileWriteAck = 0x8024;
    public const ushort FileCommit = 0x0025;
    public const ushort FileWriteComplete = 0x8025;
    public const ushort FileOpenRead = 0x0026;
    public const ushort FileReadChunk = 0x9026;
    public const ushort FileReadComplete = 0x8026;
    public const ushort DownloadRequest = 0x0027;
    public const ushort DownloadProgress = 0x9027;
    public const ushort DownloadComplete = 0x8027;
    public const ushort FileDeleteRequest = 0x0028;
    public const ushort FileDeleteResponse = 0x8028;
    public const ushort FileHashRequest = 0x0029;
    public const ushort FileHashResponse = 0x8029;
    public const ushort FileReadTextRequest = 0x002A;
    public const ushort FileReadTextResponse = 0x802A;

    public const ushort InfoRequest = 0x0030;
    public const ushort InfoResponse = 0x8030;

    public const ushort RebootRequest = 0x0040;
    public const ushort RebootAccepted = 0x8040;
    public const ushort ShutdownRequest = 0x0041;
    public const ushort ShutdownAccepted = 0x8041;
    public const ushort UpdateRequest = 0x0042;
    public const ushort UpdateAccepted = 0x8042;
    public const ushort ResetRequest = 0x0043;
    public const ushort ResetAccepted = 0x8043;

    public const ushort ScreenshotRequest = 0x0050;
    public const ushort ScreenshotResponse = 0x8050;

    public const ushort InputRequest = 0x0060;
    public const ushort InputResponse = 0x8060;
    public const ushort WindowListRequest = 0x0062;
    public const ushort WindowListResponse = 0x8062;

    public const ushort InventoryRequest = 0x0070;
    public const ushort InventoryResponse = 0x8070;

    public const ushort ProcessListRequest = 0x0080;
    public const ushort ProcessListResponse = 0x8080;
    public const ushort ProcessKillRequest = 0x0081;
    public const ushort ProcessKillResponse = 0x8081;

    public const ushort RegQueryRequest = 0x0090;
    public const ushort RegQueryResponse = 0x8090;

    public const ushort ErrorResponse = 0x8FFF;
}
