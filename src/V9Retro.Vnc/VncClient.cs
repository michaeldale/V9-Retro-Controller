using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using V9Retro.Vnc.Decoding;

namespace V9Retro.Vnc;

/// <summary>
/// A UI-free RFB (VNC) client for V9XVNC.EXE and other RFB 3.3/3.7/3.8 servers.
/// Connect, then watch <see cref="Framebuffer"/>; updates are requested and decoded on a
/// background task. Events fire on that task's thread, so a UI must marshal them.
/// </summary>
public sealed class VncClient : IAsyncDisposable
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TcpClient? _tcp;
    private Stream? _stream;
    private RfbReader? _reader;
    private ZrleDecoder? _zrle;
    private Task? _receiveLoop;
    private int _disconnected;
    private int _disposed;

    /// <summary>
    /// The encodings sent in SetEncodings, most preferred first. Change before connecting.
    /// The default is ZRLE, Hextile, CopyRect, Raw, DesktopSize, RichCursor, PointerPos.
    /// </summary>
    public IReadOnlyList<int> Encodings { get; set; } =
    [
        VncEncoding.Zrle, VncEncoding.Hextile, VncEncoding.CopyRect, VncEncoding.Raw,
        VncEncoding.DesktopSize, VncEncoding.RichCursor, VncEncoding.PointerPos,
    ];

    /// <summary>
    /// The remote screen. One instance for the client's lifetime, so handlers can be attached
    /// before connecting; it is 0x0 until ServerInit, which resizes it (raising Resized).
    /// </summary>
    public Framebuffer Framebuffer { get; } = new(0, 0);

    /// <summary>The desktop name from ServerInit (V9XVNC.EXE sends "V9x " and the computer name).</summary>
    public string DesktopName { get; private set; } = "";

    /// <summary>The RFB minor version in use: 3, 7 or 8.</summary>
    public int ProtocolMinorVersion { get; private set; }

    /// <summary>The server's native pixel format from ServerInit (the client then asks for <see cref="VncPixelFormat.Bgrx32"/>).</summary>
    public VncPixelFormat? ServerPixelFormat { get; private set; }

    /// <summary>True from a successful connect until the connection ends.</summary>
    public bool IsConnected => _receiveLoop != null && Volatile.Read(ref _disconnected) == 0;

    /// <summary>The server put text on its clipboard (Latin-1, LF line ends).</summary>
    public event Action<string>? ServerClipboard;

    /// <summary>The server rang the bell.</summary>
    public event Action? Bell;

    /// <summary>
    /// The connection ended. The argument is null after <see cref="DisposeAsync"/>, else the
    /// cause (a <see cref="VncException"/>, or an <see cref="IOException"/> from the socket).
    /// </summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>Connects over TCP, negotiates version and security, and starts receiving updates.</summary>
    /// <exception cref="VncViewerBusyException">Another viewer is connected.</exception>
    /// <exception cref="VncAuthenticationException">Wrong or missing password.</exception>
    /// <exception cref="VncConnectionRefusedException">The server refused with some other reason.</exception>
    public async Task ConnectAsync(string host, int port, string? password, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            _tcp = tcp;
            await ConnectAsync(tcp.GetStream(), password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            _tcp = null;
            throw;
        }
    }

    /// <summary>
    /// Runs the RFB session over an already-open stream (a tunnel, or a test pipe). The
    /// client owns the stream from here on and disposes it.
    /// </summary>
    public async Task ConnectAsync(Stream stream, string? password, CancellationToken cancellationToken = default)
    {
        if (_stream != null) throw new InvalidOperationException("This client is already connected.");
        _stream = stream;
        var input = new BufferedStream(stream, 64 * 1024);
        try
        {
            await HandshakeAsync(input, password, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new VncException("The VNC server closed the connection during the handshake.", e);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _reader = new RfbReader(input);
        _receiveLoop = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private async Task HandshakeAsync(Stream input, string? password, CancellationToken ct)
    {
        var buffer = new byte[16];
        await input.ReadExactlyAsync(buffer.AsMemory(0, 12), ct).ConfigureAwait(false);
        string version = Encoding.ASCII.GetString(buffer, 0, 12);
        if (!version.StartsWith("RFB 003.", StringComparison.Ordinal) || version[11] != '\n' ||
            !int.TryParse(version.AsSpan(8, 3), out int serverMinor))
            throw new VncProtocolException($"Not an RFB 3.x server (greeting \"{version.TrimEnd()}\").");
        int minor = serverMinor >= 8 ? 8 : serverMinor == 7 ? 7 : 3;
        ProtocolMinorVersion = minor;
        await SendAsync(Encoding.ASCII.GetBytes($"RFB 003.00{minor}\n"), ct).ConfigureAwait(false);

        int security;
        if (minor == 3)
        {
            security = (int)await ReadU32Async(input, buffer, ct).ConfigureAwait(false);
            if (security == 0) throw Refused(await ReadReasonAsync(input, buffer, ct).ConfigureAwait(false));
            if (security == 2 && password == null)
                throw new VncAuthenticationException("The VNC server requires a password.", passwordRequired: true);
        }
        else
        {
            await input.ReadExactlyAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            int count = buffer[0];
            if (count == 0) throw Refused(await ReadReasonAsync(input, buffer, ct).ConfigureAwait(false));
            var offered = new byte[count];
            await input.ReadExactlyAsync(offered, ct).ConfigureAwait(false);
            if (offered.Contains((byte)2) && password != null) security = 2;
            else if (offered.Contains((byte)1)) security = 1;
            else if (offered.Contains((byte)2))
                throw new VncAuthenticationException("The VNC server requires a password.", passwordRequired: true);
            else
                throw new VncProtocolException($"No supported security type (offered {string.Join(", ", offered)}).");
            await SendAsync([(byte)security], ct).ConfigureAwait(false);
        }

        if (security == 2)
        {
            var challenge = new byte[16];
            await input.ReadExactlyAsync(challenge, ct).ConfigureAwait(false);
            await SendAsync(VncAuth.Response(password, challenge), ct).ConfigureAwait(false);
        }
        else if (security != 1)
        {
            throw new VncProtocolException($"Unsupported security type {security}.");
        }

        // SecurityResult: always in 3.8, otherwise only after VNC authentication.
        if (minor == 8 || security == 2)
        {
            uint result = await ReadU32Async(input, buffer, ct).ConfigureAwait(false);
            if (result != 0)
            {
                string reason = minor == 8 ? await ReadReasonAsync(input, buffer, ct).ConfigureAwait(false) : "";
                if (security == 2)
                    throw new VncAuthenticationException(
                        string.IsNullOrEmpty(reason) ? "Wrong VNC password." : $"Wrong VNC password ({reason}).");
                throw Refused(reason);
            }
        }

        await SendAsync([1], ct).ConfigureAwait(false); // ClientInit: shared

        var init = new byte[24];
        await input.ReadExactlyAsync(init, ct).ConfigureAwait(false);
        int width = BinaryPrimitives.ReadUInt16BigEndian(init);
        int height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        ServerPixelFormat = VncPixelFormat.Parse(init.AsSpan(4, 16));
        uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        if (nameLength > 64 * 1024) throw new VncProtocolException("ServerInit desktop name is too long.");
        var name = new byte[nameLength];
        await input.ReadExactlyAsync(name, ct).ConfigureAwait(false);
        DesktopName = Latin1.GetString(name);
        Framebuffer.Resize(width, height);

        var format = new byte[20];
        format[0] = 0; // SetPixelFormat
        VncPixelFormat.Bgrx32.Write(format.AsSpan(4));
        var encodings = new byte[4 + 4 * Encodings.Count];
        encodings[0] = 2; // SetEncodings
        BinaryPrimitives.WriteUInt16BigEndian(encodings.AsSpan(2), (ushort)Encodings.Count);
        for (int i = 0; i < Encodings.Count; i++)
            BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(4 + 4 * i), Encodings[i]);
        await SendAsync([.. format, .. encodings, .. UpdateRequest(false, width, height)], ct).ConfigureAwait(false);
    }

    private static VncConnectionRefusedException Refused(string reason)
    {
        bool busy = reason.Contains("another", StringComparison.OrdinalIgnoreCase) &&
                    reason.Contains("viewer", StringComparison.OrdinalIgnoreCase);
        return busy ? new VncViewerBusyException(reason) : new VncConnectionRefusedException(reason);
    }

    private static async Task<uint> ReadU32Async(Stream input, byte[] buffer, CancellationToken ct)
    {
        await input.ReadExactlyAsync(buffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static async Task<string> ReadReasonAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        uint length = await ReadU32Async(input, buffer, ct).ConfigureAwait(false);
        if (length > 64 * 1024) throw new VncProtocolException("Reason string is too long.");
        var text = new byte[length];
        await input.ReadExactlyAsync(text, ct).ConfigureAwait(false);
        return Latin1.GetString(text);
    }

    private static byte[] UpdateRequest(bool incremental, int width, int height)
    {
        var message = new byte[10];
        message[0] = 3;
        message[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)height);
        return message;
    }

    private void ReceiveLoop()
    {
        Exception? failure = null;
        try
        {
            var reader = _reader!;
            while (!_stop.IsCancellationRequested)
            {
                byte type = reader.ReadU8();
                switch (type)
                {
                    case 0:
                        bool full = ReadFramebufferUpdate(reader);
                        var fb = Framebuffer;
                        Send(UpdateRequest(!full, fb.Width, fb.Height));
                        break;
                    case 1: // SetColourMapEntries: not used with a true-colour format
                        reader.Skip(1);
                        reader.ReadU16();
                        reader.Skip(6L * reader.ReadU16());
                        break;
                    case 2:
                        Bell?.Invoke();
                        break;
                    case 3:
                        reader.Skip(3);
                        uint length = reader.ReadU32();
                        if (length > 16 * 1024 * 1024) throw new VncProtocolException("ServerCutText is too long.");
                        var text = new byte[length];
                        reader.ReadExactly(text);
                        ServerClipboard?.Invoke(Latin1.GetString(text).Replace("\r\n", "\n"));
                        break;
                    default:
                        throw new VncProtocolException($"Unknown server message type {type}.");
                }
            }
        }
        catch (Exception e)
        {
            if (!_stop.IsCancellationRequested)
                failure = e is EndOfStreamException ? new VncException("The VNC server closed the connection.", e) : e;
        }
        RaiseDisconnected(failure);
    }

    /// <summary>Reads one FramebufferUpdate. Returns true when the next request should be a full one.</summary>
    private bool ReadFramebufferUpdate(RfbReader reader)
    {
        reader.Skip(1);
        int count = reader.ReadU16();
        var dirty = new List<VncRect>(count);
        bool resized = false;
        for (int i = 0; i < count; i++)
        {
            var rect = new VncRect(reader.ReadU16(), reader.ReadU16(), reader.ReadU16(), reader.ReadU16());
            int encoding = reader.ReadS32();
            var fb = Framebuffer;
            switch (encoding)
            {
                case VncEncoding.Raw:
                    RawDecoder.Decode(reader, fb, rect);
                    dirty.Add(rect);
                    break;
                case VncEncoding.CopyRect:
                    CopyRectDecoder.Decode(reader, fb, rect);
                    dirty.Add(rect);
                    break;
                case VncEncoding.Hextile:
                    HextileDecoder.Decode(reader, fb, rect);
                    dirty.Add(rect);
                    break;
                case VncEncoding.Zrle:
                    (_zrle ??= new ZrleDecoder()).Decode(reader, fb, rect);
                    dirty.Add(rect);
                    break;
                case VncEncoding.DesktopSize:
                    fb.Resize(rect.Width, rect.Height);
                    dirty.Clear();
                    resized = true;
                    break;
                case VncEncoding.RichCursor:
                    fb.SetCursor(ReadRichCursor(reader, rect));
                    break;
                case VncEncoding.PointerPos:
                    fb.SetCursorPosition(new VncPoint(rect.X, rect.Y));
                    break;
                default:
                    throw new VncProtocolException($"The server sent unrequested encoding {encoding}.");
            }
        }
        if (dirty.Count > 0) Framebuffer.RaiseUpdated(dirty);
        return resized;
    }

    internal static CursorShape ReadRichCursor(RfbReader reader, VncRect rect)
    {
        int w = rect.Width, h = rect.Height;
        var pixels = new uint[w * h];
        reader.ReadPixels(pixels);
        int rowBytes = (w + 7) / 8;
        var mask = new byte[rowBytes * h];
        reader.ReadExactly(mask);
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool opaque = (mask[y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0;
                uint p = opaque ? pixels[y * w + x] : 0u;
                BinaryPrimitives.WriteUInt32LittleEndian(bgra.AsSpan((y * w + x) * 4), p);
            }
        }
        return new CursorShape(w, h, new VncPoint(rect.X, rect.Y), bgra);
    }

    /// <summary>Sends a key press or release (an X11 keysym; see <see cref="KeysymMap"/>).</summary>
    public Task SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken = default)
        => SendAsync(KeyEvent(keysym, down), cancellationToken);

    /// <summary>Sends the pointer position and button state (bit 0 left, 1 middle, 2 right, 3/4 wheel up/down).</summary>
    public Task SendPointerAsync(int x, int y, byte buttonMask, CancellationToken cancellationToken = default)
    {
        var message = new byte[6];
        message[0] = 5;
        message[1] = buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)Math.Clamp(x, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
        return SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Presses and releases Ctrl+Alt+Delete as one write. V9XVNC.EXE injects it as ordinary keys:
    /// that reaches a Windows 9x close-program dialog, but cannot raise the NT secure attention sequence.
    /// </summary>
    public Task SendCtrlAltDelAsync(CancellationToken cancellationToken = default) => SendAsync(
    [
        .. KeyEvent(KeysymMap.ControlL, true), .. KeyEvent(KeysymMap.AltL, true), .. KeyEvent(KeysymMap.Delete, true),
        .. KeyEvent(KeysymMap.Delete, false), .. KeyEvent(KeysymMap.AltL, false), .. KeyEvent(KeysymMap.ControlL, false),
    ], cancellationToken);

    /// <summary>Puts text on the server's clipboard. RFB carries Latin-1; other characters become '?'.</summary>
    public Task SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Latin1.GetBytes(text.Replace("\r\n", "\n"));
        var message = new byte[8 + bytes.Length];
        message[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)bytes.Length);
        bytes.CopyTo(message, 8);
        return SendAsync(message, cancellationToken);
    }

    /// <summary>Asks for the whole screen again (a non-incremental update request).</summary>
    public Task RequestFullUpdateAsync(CancellationToken cancellationToken = default)
    {
        var fb = Framebuffer;
        return SendAsync(UpdateRequest(false, fb.Width, fb.Height), cancellationToken);
    }

    private static byte[] KeyEvent(uint keysym, bool down)
    {
        var message = new byte[8];
        message[0] = 4;
        message[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), keysym);
        return message;
    }

    private async Task SendAsync(byte[] message, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("The client is not connected.");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(message, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // The receive loop runs on its own thread, so it can wait for the send lock.
    private void Send(byte[] message) => SendAsync(message, CancellationToken.None).GetAwaiter().GetResult();

    private void RaiseDisconnected(Exception? failure)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        _zrle?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
        Disconnected?.Invoke(failure);
    }

    /// <summary>Closes the connection and waits for the receive task to end.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        if (_receiveLoop != null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop reports through Disconnected; nothing to rethrow here.
            }
        }
        _sendLock.Dispose();
        _stop.Dispose();
    }
}
