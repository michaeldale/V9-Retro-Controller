using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using V9Retro.Protocol.Wire;

namespace V9Retro.Protocol;

public sealed record V9xSessionOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Default wait for one response frame. Long operations use their own.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public string ClientLabel { get; init; } = "v9retro";

    public static readonly V9xSessionOptions Default = new();
}

/// <summary>One TCP connection to an agent, after HELLO. Operations are serialised: one
/// request is in flight at a time (EXEC holds the session until it completes, while CANCEL
/// can still be written). The agent has only four connection slots, so dispose promptly.</summary>
public sealed class V9xSession : IAsyncDisposable
{
    public const string DefaultScreenshotPath = @"C:\V9XREMOTE\TEMP\SCREEN.BMP";
    public const int ChunkSize = 32768;
    public const long MaxFileSize = int.MaxValue;

    readonly Stream _stream;
    readonly Socket? _socket;
    readonly V9xSessionOptions _options;
    readonly SemaphoreSlim _opLock = new(1, 1);
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly byte[] _header = new byte[FrameHeader.Size];
    uint _requestId;
    volatile bool _broken;
    int _disposed;

    V9xSession(Stream stream, Socket? socket, string host, int port, V9xSessionOptions options)
    {
        _stream = stream;
        _socket = socket;
        _options = options;
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public HelloInfo Hello { get; private set; } = null!;
    public Capabilities Capabilities => Hello.Capabilities;
    public bool IsBroken => _broken;

    /// <summary>Opens a direct TCP connection (TCP_NODELAY) to an agent.</summary>
    public static async Task<Stream> OpenTcpAsync(string host, int port, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(connectTimeout);
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new V9xTransportException($"connect to {host}:{port} timed out");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new V9xTransportException($"connect to {host}:{port} failed: {ex.Message}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<V9xSession> ConnectAsync(string host, int port = FrameHeader.DefaultPort,
        V9xSessionOptions? options = null, CancellationToken ct = default)
    {
        options ??= V9xSessionOptions.Default;
        var stream = await OpenTcpAsync(host, port, options.ConnectTimeout, ct).ConfigureAwait(false);
        return await ConnectAsync(stream, host, port, options, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the protocol over an already-open stream (a direct socket, or a tunnel through
    /// a host). The session owns the stream from here on.</summary>
    public static async Task<V9xSession> ConnectAsync(Stream stream, string host, int port, V9xSessionOptions? options = null,
        CancellationToken ct = default)
    {
        var session = new V9xSession(stream, (stream as NetworkStream)?.Socket, host, port, options ?? V9xSessionOptions.Default);
        try
        {
            await session.HandshakeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    async Task HandshakeAsync(CancellationToken ct)
    {
        var payload = new PayloadWriter()
            .U16(FrameHeader.ProtocolVersion).U16(FrameHeader.ProtocolVersion)
            .String(_options.ClientLabel);
        Frame frame;
        try
        {
            frame = await RequestAsync(MessageType.HelloRequest, payload.Memory, [MessageType.HelloResponse], null, ct)
                .ConfigureAwait(false);
        }
        catch (V9xTransportException ex) when (ex.InnerException is EndOfStreamException or IOException or SocketException)
        {
            // The agent accepts then closes a fifth connection, or a client outside allowed_client
            // (seen as a clean close or, on Windows, a reset).
            throw new V9xTransportException(
                "agent closed the connection before HELLO (all 4 connection slots busy, or this PC is not the allowed client)",
                ex);
        }
        Hello = Parsers.ParseHello(frame.Payload);
        if (Hello.ProtocolVersion != FrameHeader.ProtocolVersion)
            throw new V9xProtocolException("no compatible protocol version");
    }

    // ---- framing ---------------------------------------------------------------------

    uint NextId()
    {
        _requestId = _requestId >= 0x7FFFFFFE ? 1 : _requestId + 1;
        return _requestId;
    }

    async Task SendAsync(ushort type, uint requestId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > FrameHeader.MaxPayload)
            throw new ArgumentException("payload exceeds the protocol limit");
        var buffer = new byte[FrameHeader.Size + payload.Length];
        new FrameHeader(type, requestId, 0, payload.Length).Write(buffer);
        payload.Span.CopyTo(buffer.AsSpan(FrameHeader.Size));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _broken = true;
            throw new V9xTransportException($"send failed: {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task<Frame> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _stream.ReadExactlyAsync(_header, cts.Token).ConfigureAwait(false);
            var header = FrameHeader.Read(_header);
            var payload = header.PayloadLength == 0 ? [] : new byte[header.PayloadLength];
            if (payload.Length > 0)
                await _stream.ReadExactlyAsync(payload, cts.Token).ConfigureAwait(false);
            return new Frame(header, payload);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _broken = true;
            throw new V9xTransportException($"no response from {Host}:{Port} within {timeout.TotalSeconds:0} s");
        }
        catch (OperationCanceledException)
        {
            _broken = true; // a frame may be half read
            throw;
        }
        catch (EndOfStreamException ex)
        {
            _broken = true;
            throw new V9xTransportException("peer closed the connection", ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _broken = true;
            throw new V9xTransportException($"receive failed: {ex.Message}", ex);
        }
        catch (V9xProtocolException)
        {
            _broken = true;
            throw;
        }
    }

    /// <summary>Next frame for <paramref name="requestId"/>; frames for other IDs (stale replies,
    /// CANCEL acknowledgements) are skipped. ERROR_RESPONSE becomes a <see cref="V9xGuestException"/>.</summary>
    async Task<Frame> ExpectAsync(uint requestId, ushort[] types, TimeSpan? timeout, CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReceiveAsync(timeout ?? _options.ReadTimeout, ct).ConfigureAwait(false);
            if (frame.RequestId != requestId) continue;
            if (frame.Type == MessageType.ErrorResponse) throw Parsers.ParseError(frame.Payload);
            if (Array.IndexOf(types, frame.Type) < 0)
            {
                _broken = true;
                throw new V9xProtocolException($"unexpected response type 0x{frame.Type:X4}");
            }
            return frame;
        }
    }

    async Task<Frame> RequestAsync(ushort type, ReadOnlyMemory<byte> payload, ushort[] responses,
        TimeSpan? timeout, CancellationToken ct)
    {
        var id = NextId();
        await SendAsync(type, id, payload, ct).ConfigureAwait(false);
        return await ExpectAsync(id, responses, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one exclusive operation, failing fast when the connection is already broken.</summary>
    async Task<T> OpAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_broken) throw new V9xTransportException("connection is no longer usable");
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    Task<Frame> SimpleAsync(ushort type, ReadOnlyMemory<byte> payload, ushort response, CancellationToken ct,
        TimeSpan? timeout = null) =>
        OpAsync(() => RequestAsync(type, payload, [response], timeout, ct), ct);

    void Require(Capabilities caps, string what)
    {
        if (!Capabilities.Has(caps))
            throw new V9xUnsupportedException($"agent build '{Hello.BuildId}' does not support {what}");
    }

    static ReadOnlyMemory<byte> Str(string value) => new PayloadWriter().String(value).Memory;

    // ---- status ----------------------------------------------------------------------

    public async Task<PingResult> PingAsync(CancellationToken ct = default) =>
        Parsers.ParsePing((await SimpleAsync(MessageType.PingRequest, default, MessageType.PingResponse, ct)
            .ConfigureAwait(false)).Payload);

    /// <summary>INFO; asks for the install ID when the agent advertises <see cref="Capabilities.InstallId"/>.</summary>
    public async Task<AgentInfo> InfoAsync(CancellationToken ct = default)
    {
        Require(Capabilities.Info, "INFO");
        var extended = Capabilities.Has(Capabilities.InstallId);
        var payload = extended ? new PayloadWriter().U32(1).Memory : default;
        var frame = await SimpleAsync(MessageType.InfoRequest, payload, MessageType.InfoResponse, ct)
            .ConfigureAwait(false);
        return Parsers.ParseInfo(frame.Payload, extended);
    }

    public async Task<IReadOnlyList<InventoryRecord>> InventoryAsync(CancellationToken ct = default)
    {
        Require(Capabilities.Inventory, "inventory (agent 0.7.0+)");
        var frame = await SimpleAsync(MessageType.InventoryRequest, default, MessageType.InventoryResponse, ct,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return Parsers.ParseInventory(frame.Payload);
    }

    // ---- execution -------------------------------------------------------------------

    /// <summary>Starts a command and streams its output. Cancelling <paramref name="ct"/> sends
    /// CANCEL and keeps reading until the agent reports completion.</summary>
    public async IAsyncEnumerable<ExecEvent> ExecStreamAsync(ExecRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Require(request.Mode == ExecMode.Direct ? Capabilities.ExecDirect : Capabilities.ExecShell, "execution");
        if (request.Detach) Require(Capabilities.ExecDetach, "detached execution");
        if (request.Mode == ExecMode.Direct && request.Application.Length == 0)
            throw new ArgumentException("direct mode needs an application");
        var payload = new PayloadWriter()
            .U8((byte)request.Mode)
            .U8(request.ShowWindow ? (byte)1 : (byte)0)
            .U16(request.Detach ? (ushort)1 : (ushort)0)
            .U32(Math.Min(request.TimeoutMs, ExecRequest.MaxTimeoutMs))
            .U32(Math.Min(request.StdoutLimit, ExecRequest.MaxOutputLimit))
            .U32(Math.Min(request.StderrLimit, ExecRequest.MaxOutputLimit))
            .String(request.Application)
            .String(request.Arguments)
            .String(request.WorkingDirectory);

        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_broken) throw new V9xTransportException("connection is no longer usable");
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = NextId();
            var frameTimeout = TimeSpan.FromMilliseconds(Math.Min(request.TimeoutMs, ExecRequest.MaxTimeoutMs)) + TimeSpan.FromSeconds(20);
            await SendAsync(MessageType.ExecRequest, id, payload.Memory, ct).ConfigureAwait(false);
            var accepted = await ExpectAsync(id, [MessageType.ExecAccepted], null, ct).ConfigureAwait(false);
            if (accepted.Payload.Length != 4) throw new V9xProtocolException("invalid EXEC_ACCEPTED payload");
            yield return new ExecEvent.Accepted(id);

            var cancelSent = 0;
            using var registration = ct.Register(() =>
            {
                if (Interlocked.Exchange(ref cancelSent, 1) == 0)
                    _ = SendCancelAsync(id);
            });
            while (true)
            {
                // Reads ignore ct: after a cancel the agent still sends EXEC_COMPLETE.
                var frame = await ExpectAsync(id, [MessageType.ExecStdout, MessageType.ExecStderr, MessageType.ExecComplete],
                    frameTimeout, CancellationToken.None).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case MessageType.ExecStdout: yield return new ExecEvent.Stdout(frame.Payload); break;
                    case MessageType.ExecStderr: yield return new ExecEvent.Stderr(frame.Payload); break;
                    default:
                        yield return new ExecEvent.Completed(Parsers.ParseExecComplete(frame.Payload));
                        yield break;
                }
            }
        }
        finally
        {
            _opLock.Release();
        }
    }

    async Task SendCancelAsync(uint targetId)
    {
        try
        {
            await SendAsync(MessageType.CancelRequest, NextCancelId(), new PayloadWriter().U32(targetId).Memory,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (V9xException)
        {
            // The read loop reports the broken connection.
        }
    }

    // CANCEL uses an ID from the top of the range so it never collides with the op counter.
    uint _cancelId = 0x7FFFFFFF;
    uint NextCancelId() => Interlocked.Decrement(ref _cancelId);

    /// <summary>Runs a command to completion and decodes its output as guest ANSI text.</summary>
    public async Task<(ExecResult Result, string Stdout, string Stderr)> ExecAsync(ExecRequest request,
        CancellationToken ct = default)
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        ExecResult? result = null;
        await foreach (var e in ExecStreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (e)
            {
                case ExecEvent.Stdout o: stdout.Write(o.Data); break;
                case ExecEvent.Stderr o: stderr.Write(o.Data); break;
                case ExecEvent.Completed c: result = c.Result; break;
            }
        }
        return (result ?? throw new V9xProtocolException("EXEC ended without EXEC_COMPLETE"),
            GuestText.Decode(stdout.ToArray()), GuestText.Decode(stderr.ToArray()));
    }

    // ---- files -----------------------------------------------------------------------

    public async Task<FileStat> StatAsync(string guestPath, CancellationToken ct = default)
    {
        Require(Capabilities.FileRead, "file access");
        var frame = await SimpleAsync(MessageType.FileStatRequest, Str(guestPath), MessageType.FileStatResponse, ct)
            .ConfigureAwait(false);
        return Parsers.ParseStat(frame.Payload);
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string guestDirectory, CancellationToken ct = default)
    {
        Require(Capabilities.FileRead, "file access");
        var frame = await SimpleAsync(MessageType.FileListRequest, Str(guestDirectory), MessageType.FileListResponse, ct)
            .ConfigureAwait(false);
        return Parsers.ParseList(frame.Payload);
    }

    /// <summary>Returns true when created, false when it already existed.</summary>
    public async Task<bool> MkdirAsync(string guestPath, CancellationToken ct = default)
    {
        Require(Capabilities.FileWrite, "file writes");
        var frame = await SimpleAsync(MessageType.FileMkdirRequest, Str(guestPath), MessageType.FileMkdirResponse, ct)
            .ConfigureAwait(false);
        if (frame.Payload.Length != 4) throw new V9xProtocolException("invalid FILE_MKDIR_RESPONSE payload");
        return BitConverter.ToUInt32(frame.Payload) != 0;
    }

    public async Task<FileDeleteResult> DeleteAsync(string guestPath, bool recursive = false, bool force = false,
        CancellationToken ct = default)
    {
        Require(Capabilities.FileTools, "delete (agent 0.13.0+)");
        var payload = new PayloadWriter().U32((recursive ? 1u : 0) | (force ? 2u : 0)).String(guestPath).Memory;
        var frame = await SimpleAsync(MessageType.FileDeleteRequest, payload, MessageType.FileDeleteResponse, ct,
            TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        if (frame.Payload.Length != 8) throw new V9xProtocolException("invalid FILE_DELETE_RESPONSE");
        return new FileDeleteResult(BitConverter.ToUInt32(frame.Payload, 0), BitConverter.ToUInt32(frame.Payload, 4));
    }

    public async Task<FileHashResult> HashAsync(string guestPath, CancellationToken ct = default)
    {
        Require(Capabilities.FileTools, "hash (agent 0.13.0+)");
        var frame = await SimpleAsync(MessageType.FileHashRequest, Str(guestPath), MessageType.FileHashResponse, ct,
            TimeSpan.FromSeconds(300)).ConfigureAwait(false);
        if (frame.Payload.Length != 40) throw new V9xProtocolException("invalid FILE_HASH_RESPONSE");
        return new FileHashResult(BitConverter.ToUInt32(frame.Payload, 0), BitConverter.ToUInt32(frame.Payload, 4),
            frame.Payload.AsSpan(8, 32).ToArray());
    }

    /// <param name="tail">Last N matching lines; 0 = all.</param>
    /// <param name="match">Case-insensitive ASCII substring, at most 128 bytes; empty = every line.</param>
    public async Task<ReadTextResult> ReadTextAsync(string guestPath, uint tail = 0, string match = "",
        uint maxBytes = 65512, CancellationToken ct = default)
    {
        Require(Capabilities.FileTools, "read text (agent 0.13.0+)");
        var payload = new PayloadWriter().U32(tail).U32(maxBytes).String(match).String(guestPath).Memory;
        var frame = await SimpleAsync(MessageType.FileReadTextRequest, payload, MessageType.FileReadTextResponse, ct,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return Parsers.ParseReadText(frame.Payload);
    }

    /// <summary>Uploads a seekable stream: one pass for the CRC32, then 32 KiB chunks, each
    /// acknowledged, then COMMIT. The guest replaces the destination only after validating.</summary>
    public Task<TransferResult> UploadAsync(Stream source, string guestPath, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        Require(Capabilities.FileWrite, "file writes");
        if (!source.CanSeek) throw new ArgumentException("upload source must be seekable", nameof(source));
        var start = source.Position;
        var size = source.Length - start;
        if (size > MaxFileSize) throw new ArgumentException("file exceeds the 2 GiB protocol limit");
        return OpAsync(async () =>
        {
            var buffer = new byte[ChunkSize];
            uint crc = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                crc = Crc32.Append(crc, buffer.AsSpan(0, read));
            source.Position = start;

            var id = NextId();
            var open = new PayloadWriter().U32((uint)size).U32(crc).String(guestPath);
            await SendAsync(MessageType.FileOpenWrite, id, open.Memory, ct).ConfigureAwait(false);
            var ready = await ExpectAsync(id, [MessageType.FileWriteReady], null, ct).ConfigureAwait(false);
            if (!ready.Payload.AsSpan().SequenceEqual(new PayloadWriter().U32((uint)size).U32(crc).Memory.Span))
                throw new V9xProtocolException("FILE_WRITE_READY did not echo size and CRC32");

            long offset = 0;
            var chunk = new byte[4 + ChunkSize];
            while (offset < size)
            {
                var want = (int)Math.Min(ChunkSize, size - offset);
                await source.ReadExactlyAsync(chunk.AsMemory(4, want), ct).ConfigureAwait(false);
                BitConverter.TryWriteBytes(chunk.AsSpan(0, 4), (uint)offset);
                await SendAsync(MessageType.FileWriteChunk, id, chunk.AsMemory(0, 4 + want), ct).ConfigureAwait(false);
                offset += want;
                var ack = await ExpectAsync(id, [MessageType.FileWriteAck], null, ct).ConfigureAwait(false);
                if (ack.Payload.Length != 4 || BitConverter.ToUInt32(ack.Payload) != (uint)offset)
                    throw new V9xProtocolException("upload acknowledgement offset mismatch");
                progress?.Report(offset);
            }
            await SendAsync(MessageType.FileCommit, id, default, ct).ConfigureAwait(false);
            var done = await ExpectAsync(id, [MessageType.FileWriteComplete], TimeSpan.FromSeconds(60), ct)
                .ConfigureAwait(false);
            if (done.Payload.Length != 8 || BitConverter.ToUInt32(done.Payload, 0) != (uint)size
                || BitConverter.ToUInt32(done.Payload, 4) != crc)
                throw new V9xProtocolException("commit did not confirm size and CRC32");
            return new TransferResult(size, crc);
        }, ct);
    }

    /// <summary>Streams a guest file into <paramref name="destination"/> and verifies size and
    /// CRC32. Callers should write to a temporary file and publish it only on success.</summary>
    public Task<TransferResult> DownloadAsync(string guestPath, Stream destination, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        Require(Capabilities.FileRead, "file reads");
        return OpAsync(async () =>
        {
            var id = NextId();
            await SendAsync(MessageType.FileOpenRead, id, Str(guestPath), ct).ConfigureAwait(false);
            long received = 0;
            uint crc = 0;
            while (true)
            {
                var frame = await ExpectAsync(id, [MessageType.FileReadChunk, MessageType.FileReadComplete], null, ct)
                    .ConfigureAwait(false);
                if (frame.Type == MessageType.FileReadComplete)
                {
                    if (frame.Payload.Length != 8) throw new V9xProtocolException("invalid FILE_READ_COMPLETE payload");
                    var size = BitConverter.ToUInt32(frame.Payload, 0);
                    var expected = BitConverter.ToUInt32(frame.Payload, 4);
                    if (size != received || expected != crc)
                        throw new V9xProtocolException("downloaded size or CRC32 does not match the guest");
                    return new TransferResult(received, crc);
                }
                if (frame.Payload.Length < 4 || BitConverter.ToUInt32(frame.Payload) != (uint)received)
                    throw new V9xProtocolException("download chunk offset mismatch");
                var data = frame.Payload.AsMemory(4);
                crc = Crc32.Append(crc, data.Span);
                await destination.WriteAsync(data, ct).ConfigureAwait(false);
                received += data.Length;
                progress?.Report(received);
            }
        }, ct);
    }

    public async Task<byte[]> DownloadBytesAsync(string guestPath, CancellationToken ct = default)
    {
        var buffer = new MemoryStream();
        await DownloadAsync(guestPath, buffer, null, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public Task<TransferResult> UploadBytesAsync(byte[] data, string guestPath, CancellationToken ct = default) =>
        UploadAsync(new MemoryStream(data, writable: false), guestPath, null, ct);

    /// <summary>The guest fetches an http:// URL itself (no https).</summary>
    public Task<DownloadResult> GuestHttpDownloadAsync(string url, string guestPath, IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        Require(Capabilities.HttpDownload, "guest HTTP download");
        return OpAsync(async () =>
        {
            var id = NextId();
            await SendAsync(MessageType.DownloadRequest, id, new PayloadWriter().String(url).String(guestPath).Memory, ct)
                .ConfigureAwait(false);
            while (true)
            {
                var frame = await ExpectAsync(id, [MessageType.DownloadProgress, MessageType.DownloadComplete],
                    TimeSpan.FromSeconds(600), ct).ConfigureAwait(false);
                if (frame.Type == MessageType.DownloadProgress)
                {
                    if (frame.Payload.Length >= 4) progress?.Report(BitConverter.ToUInt32(frame.Payload));
                    continue;
                }
                if (frame.Payload.Length != 12) throw new V9xProtocolException("invalid DOWNLOAD_COMPLETE payload");
                return new DownloadResult(BitConverter.ToUInt32(frame.Payload, 0), BitConverter.ToUInt32(frame.Payload, 4),
                    BitConverter.ToUInt32(frame.Payload, 8));
            }
        }, ct);
    }

    // ---- screen and input ------------------------------------------------------------

    /// <summary>Captures the screen to a BMP on the guest and downloads it, verifying size and CRC.
    /// Options are sent only to agents that advertise <see cref="Capabilities.ScreenshotOptions"/>.</summary>
    public async Task<(ScreenshotInfo Info, byte[] Bmp)> ScreenshotAsync(ScreenshotOptions? options = null,
        string guestPath = DefaultScreenshotPath, CancellationToken ct = default)
    {
        Require(Capabilities.ScreenshotBmp | Capabilities.FileRead, "screenshots");
        var payload = new PayloadWriter().String(guestPath);
        if (options is not null && Capabilities.Has(Capabilities.ScreenshotOptions))
            payload.U32((uint)options.Flags).U32(options.X).U32(options.Y).U32(options.Width).U32(options.Height)
                .U32(options.ScalePercent);
        var frame = await SimpleAsync(MessageType.ScreenshotRequest, payload.Memory, MessageType.ScreenshotResponse, ct,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var info = Parsers.ParseScreenshot(frame.Payload);
        if (info.FileSize > 16 * 1024 * 1024) throw new V9xProtocolException("screenshot exceeds 16 MiB");
        var bmp = await DownloadBytesAsync(info.GuestPath, ct).ConfigureAwait(false);
        if (bmp.Length != info.FileSize || Crc32.Compute(bmp) != info.Crc32)
            throw new V9xProtocolException("screenshot download does not match capture metadata");
        return (info, bmp);
    }

    public async Task<InputResult> InputAsync(IEnumerable<InputAction> actions, CancellationToken ct = default)
    {
        Require(Capabilities.InputInject, "input");
        var list = actions as IReadOnlyCollection<InputAction> ?? actions.ToList();
        if (list.Count == 0 || list.Count > InputAction.MaxActions)
            throw new ArgumentException("1 to 256 input actions per request", nameof(actions));
        if (list.OfType<InputAction.MoveBy>().Any()) Require(Capabilities.InputRelative, "relative mouse input");
        var payload = new PayloadWriter().U16((ushort)list.Count);
        foreach (var action in list) action.Write(payload);
        var frame = await SimpleAsync(MessageType.InputRequest, payload.Memory, MessageType.InputResponse, ct,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return Parsers.ParseInput(frame.Payload);
    }

    public async Task<WindowListResult> WindowListAsync(CancellationToken ct = default)
    {
        Require(Capabilities.Windows, "window list (agent 0.13.0+)");
        var frame = await SimpleAsync(MessageType.WindowListRequest, default, MessageType.WindowListResponse, ct)
            .ConfigureAwait(false);
        return Parsers.ParseWindowList(frame.Payload);
    }

    // ---- processes and registry ------------------------------------------------------

    public async Task<IReadOnlyList<ProcessEntry>> ProcessListAsync(CancellationToken ct = default)
    {
        Require(Capabilities.Process, "process list (agent 0.13.0+)");
        var frame = await SimpleAsync(MessageType.ProcessListRequest, default, MessageType.ProcessListResponse, ct)
            .ConfigureAwait(false);
        return Parsers.ParseProcessList(frame.Payload);
    }

    public async Task<ProcessKillResult> ProcessKillAsync(uint pid, uint exitCode = 1, CancellationToken ct = default)
    {
        Require(Capabilities.Process, "process kill (agent 0.13.0+)");
        var frame = await SimpleAsync(MessageType.ProcessKillRequest, new PayloadWriter().U32(pid).U32(exitCode).Memory,
            MessageType.ProcessKillResponse, ct).ConfigureAwait(false);
        if (frame.Payload.Length != 8) throw new V9xProtocolException("invalid PROCESS_KILL_RESPONSE");
        return new ProcessKillResult(BitConverter.ToUInt32(frame.Payload, 0), BitConverter.ToUInt32(frame.Payload, 4));
    }

    public async Task<RegistryQueryResult> RegQueryAsync(RegistryHive hive, string subkey, CancellationToken ct = default)
    {
        Require(Capabilities.Registry, "registry (agent 0.13.0+)");
        var frame = await SimpleAsync(MessageType.RegQueryRequest, new PayloadWriter().U32((uint)hive).String(subkey).Memory,
            MessageType.RegQueryResponse, ct).ConfigureAwait(false);
        return Parsers.ParseRegQuery(frame.Payload);
    }

    // ---- power and update ------------------------------------------------------------

    /// <summary>A fresh resume token: the guest saves it and reports it as pending_job after the
    /// restart, which together with a new boot counter proves the reboot happened.</summary>
    public static string NewPowerToken() => "v9r" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(7)).ToLowerInvariant();

    public Task<PowerAccepted> RebootAsync(string token, CancellationToken ct = default) =>
        PowerAsync(MessageType.RebootRequest, MessageType.RebootAccepted, Capabilities.PowerControl, token, ct);

    public Task<PowerAccepted> ShutdownAsync(string token, CancellationToken ct = default) =>
        PowerAsync(MessageType.ShutdownRequest, MessageType.ShutdownAccepted, Capabilities.PowerControl, token, ct);

    /// <summary>Hard reset through port CF9h (Win9x, agent 0.10.0+).</summary>
    public Task<PowerAccepted> ResetAsync(string token, CancellationToken ct = default) =>
        PowerAsync(MessageType.ResetRequest, MessageType.ResetAccepted, Capabilities.HardReset, token, ct);

    async Task<PowerAccepted> PowerAsync(ushort request, ushort response, Capabilities cap, string token, CancellationToken ct)
    {
        Require(cap, "power control");
        if (token.Length is 0 or > 63 || Encoding.ASCII.GetByteCount(token) != token.Length)
            throw new ArgumentException("token must be 1 to 63 ASCII characters", nameof(token));
        var frame = await SimpleAsync(request, Str(token), response, ct).ConfigureAwait(false);
        var accepted = Parsers.ParsePowerAccepted(frame.Payload);
        if (accepted.Token.Length > 0 && accepted.Token != token)
            throw new V9xProtocolException("power acceptance token does not match");
        return accepted;
    }

    /// <summary>Applies agent and helper binaries already uploaded as C:\V9XREMOTE\V9XNEW.EXE and
    /// V9XSNEW.EXE. Check <see cref="AgentFeatures.CanHotUpdate"/> first.</summary>
    public async Task<UpdateAcceptedResult> ApplyUpdateAsync(uint agentSize, uint agentCrc, uint helperSize, uint helperCrc,
        CancellationToken ct = default)
    {
        Require(Capabilities.DriverUpdate, "hot update");
        var payload = new PayloadWriter().U32(agentSize).U32(agentCrc).U32(helperSize).U32(helperCrc).Memory;
        var frame = await SimpleAsync(MessageType.UpdateRequest, payload, MessageType.UpdateAccepted, ct,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (frame.Payload.Length != 8) throw new V9xProtocolException("invalid UPDATE_ACCEPTED payload");
        return new UpdateAcceptedResult(BitConverter.ToUInt32(frame.Payload, 0), BitConverter.ToUInt32(frame.Payload, 4));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _broken = true;
        try { _socket?.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
