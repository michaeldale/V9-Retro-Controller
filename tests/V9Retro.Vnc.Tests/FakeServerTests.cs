using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace V9Retro.Vnc.Tests;

/// <summary>Drives VncClient against an in-process RFB server scripted like V9XVNC.EXE.</summary>
public class FakeServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Accepts one connection on loopback and runs <paramref name="script"/> on it.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public Task Done { get; }

        public FakeServer(Func<Stream, Task> script)
        {
            _listener.Start();
            Done = Task.Run(async () =>
            {
                using var socket = await _listener.AcceptTcpClientAsync();
                await using var stream = socket.GetStream();
                await script(stream);
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await Done.WaitAsync(Timeout); } catch { /* the test asserts on Done itself */ }
        }
    }

    private static async Task<byte[]> Read(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Timeout);
        return buffer;
    }

    private static Task Write(Stream stream, Wire wire) => stream.WriteAsync(wire.ToArray()).AsTask();

    private static async Task<string> ReadVersion(Stream stream) => Encoding.ASCII.GetString(await Read(stream, 12));

    /// <summary>ServerInit for a 4x2 screen named "V9x TEST", in the server's default format.</summary>
    private static Wire ServerInit()
    {
        var name = Encoding.ASCII.GetBytes("V9x TEST");
        return new Wire().U16(4).U16(2)
            .U8(32).U8(24).U8(0).U8(1).U16(255).U16(255).U16(255).U8(16).U8(8).U8(0).U8(0).U8(0).U8(0)
            .U32((uint)name.Length).Bytes(name);
    }

    /// <summary>Reads SetPixelFormat, SetEncodings and the first update request; checks them.</summary>
    private static async Task ExpectSetup(Stream stream)
    {
        var format = await Read(stream, 20);
        Assert.Equal(0, format[0]);
        Assert.Equal(VncPixelFormat.Bgrx32, VncPixelFormat.Parse(format.AsSpan(4)));
        var head = await Read(stream, 4);
        Assert.Equal(2, head[0]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
        var list = await Read(stream, 4 * count);
        var encodings = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32BigEndian(list.AsSpan(4 * i))).ToArray();
        Assert.Equal(new[] { 16, 5, 1, 0, -223, -239, -232 }, encodings);
        var request = await Read(stream, 10);
        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0, 0, 4, 0, 2 }, request);
    }

    /// <summary>The 3.8 handshake up to ServerInit, with None or VNC auth.</summary>
    private static async Task Handshake38(Stream stream, string? password)
    {
        await Write(stream, new Wire().Bytes(Encoding.ASCII.GetBytes("RFB 003.008\n")));
        Assert.Equal("RFB 003.008\n", await ReadVersion(stream));
        await Write(stream, new Wire().U8(1).U8(password == null ? 1 : 2));
        Assert.Equal(password == null ? 1 : 2, (await Read(stream, 1))[0]);
        if (password != null)
        {
            var challenge = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 1)).ToArray();
            await Write(stream, new Wire().Bytes(challenge));
            Assert.Equal(VncAuth.Response(password, challenge), await Read(stream, 16));
        }
        await Write(stream, new Wire().U32(0));
        Assert.Equal(1, (await Read(stream, 1))[0]); // shared
        await Write(stream, ServerInit());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("secret")]
    public async Task HandshakeAndOneUpdate(string? password)
    {
        var incremental = new TaskCompletionSource<byte[]>();
        var input = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async stream =>
        {
            await Handshake38(stream, password);
            await ExpectSetup(stream);
            // One update: a raw rect, the cursor shape and position. Then clipboard text.
            await Write(stream, new Wire()
                .U8(0).U8(0).U16(3)
                .Rect(1, 0, 2, 2, VncEncoding.Raw).Pixel(0xFF0000).Pixel(0x00FF00).Pixel(0x0000FF).Pixel(0xFFFFFF)
                .Rect(0, 0, 1, 1, VncEncoding.RichCursor).Pixel(0x123456).U8(0x80)
                .Rect(3, 1, 0, 0, VncEncoding.PointerPos)
                .U8(3).U8(0).U8(0).U8(0).U32(5).Bytes(Encoding.Latin1.GetBytes("héllo")));
            incremental.SetResult(await Read(stream, 10));
            // Key down 'a', pointer at 2,1 with the left button, clipboard "hi".
            input.SetResult(await Read(stream, 8 + 6 + 10));
        });

        await using var client = new VncClient();
        var updated = new TaskCompletionSource<IReadOnlyList<VncRect>>();
        var clipboard = new TaskCompletionSource<string>();
        var cursor = new TaskCompletionSource<CursorShape>();
        var moved = new TaskCompletionSource<VncPoint>();
        var disconnected = new TaskCompletionSource<Exception?>();
        client.ServerClipboard += text => clipboard.TrySetResult(text);
        client.Disconnected += e => disconnected.TrySetResult(e);
        client.Framebuffer.FramebufferUpdated += rects => updated.TrySetResult(rects);
        client.Framebuffer.CursorChanged += (shape, _) => cursor.TrySetResult(shape);
        client.Framebuffer.CursorMoved += point => moved.TrySetResult(point);

        await client.ConnectAsync("127.0.0.1", server.Port, password).WaitAsync(Timeout);

        Assert.Equal("V9x TEST", client.DesktopName);
        Assert.Equal(8, client.ProtocolMinorVersion);
        Assert.Equal(4, client.Framebuffer.Width);
        Assert.Equal(2, client.Framebuffer.Height);

        Assert.Equal([new VncRect(1, 0, 2, 2)], await updated.Task.WaitAsync(Timeout));
        Assert.Equal(0xFFFF0000u, client.Framebuffer.GetPixel(1, 0));
        Assert.Equal(0xFFFFFFFFu, client.Framebuffer.GetPixel(2, 1));
        Assert.Equal(0xFF000000u, client.Framebuffer.GetPixel(0, 0));
        var shape = await cursor.Task.WaitAsync(Timeout);
        Assert.Equal(new byte[] { 0x56, 0x34, 0x12, 0xFF }, shape.Bgra);
        Assert.Equal(new VncPoint(3, 1), await moved.Task.WaitAsync(Timeout));
        Assert.Equal("héllo", await clipboard.Task.WaitAsync(Timeout));
        Assert.Equal(new byte[] { 3, 1, 0, 0, 0, 0, 0, 4, 0, 2 }, await incremental.Task.WaitAsync(Timeout));

        await client.SendKeyAsync(KeysymMap.ToKeysym(VncKey.A)!.Value, true);
        await client.SendPointerAsync(2, 1, 1);
        await client.SendClipboardAsync("hi");
        Assert.Equal(new byte[]
        {
            4, 1, 0, 0, 0, 0, 0, (byte)'a',
            5, 1, 0, 2, 0, 1,
            6, 0, 0, 0, 0, 0, 0, 2, (byte)'h', (byte)'i',
        }, await input.Task.WaitAsync(Timeout));

        await server.Done.WaitAsync(Timeout);
        // The server closed the socket: the client reports a non-null cause.
        Assert.IsAssignableFrom<Exception>(await disconnected.Task.WaitAsync(Timeout));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Rfb33WithVncAuth()
    {
        await using var server = new FakeServer(async stream =>
        {
            await Write(stream, new Wire().Bytes(Encoding.ASCII.GetBytes("RFB 003.003\n")));
            Assert.Equal("RFB 003.003\n", await ReadVersion(stream));
            await Write(stream, new Wire().U32(2));
            var challenge = new byte[16];
            await Write(stream, new Wire().Bytes(challenge));
            Assert.Equal(VncAuth.Response("pw", challenge), await Read(stream, 16));
            await Write(stream, new Wire().U32(0));
            Assert.Equal(1, (await Read(stream, 1))[0]);
            await Write(stream, ServerInit());
            await ExpectSetup(stream);
        });
        await using var client = new VncClient();
        await client.ConnectAsync("127.0.0.1", server.Port, "pw").WaitAsync(Timeout);
        Assert.Equal(3, client.ProtocolMinorVersion);
        await server.Done.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Rfb37GreetingSkipsSecurityResultForNone()
    {
        await using var server = new FakeServer(async stream =>
        {
            await Write(stream, new Wire().Bytes(Encoding.ASCII.GetBytes("RFB 003.007\n")));
            Assert.Equal("RFB 003.007\n", await ReadVersion(stream));
            await Write(stream, new Wire().U8(1).U8(1));
            Assert.Equal(1, (await Read(stream, 1))[0]);
            Assert.Equal(1, (await Read(stream, 1))[0]); // ClientInit, no SecurityResult before it
            await Write(stream, ServerInit());
            await ExpectSetup(stream);
        });
        await using var client = new VncClient();
        await client.ConnectAsync("127.0.0.1", server.Port, null).WaitAsync(Timeout);
        Assert.Equal(7, client.ProtocolMinorVersion);
        await server.Done.WaitAsync(Timeout);
    }

    [Fact]
    public async Task WrongPasswordIsTyped()
    {
        await using var server = new FakeServer(async stream =>
        {
            await Write(stream, new Wire().Bytes(Encoding.ASCII.GetBytes("RFB 003.008\n")));
            await ReadVersion(stream);
            await Write(stream, new Wire().U8(1).U8(2));
            await Read(stream, 1);
            await Write(stream, new Wire().Bytes(new byte[16]));
            await Read(stream, 16);
            var reason = Encoding.ASCII.GetBytes("Authentication failed");
            await Write(stream, new Wire().U32(1).U32((uint)reason.Length).Bytes(reason));
        });
        await using var client = new VncClient();
        var e = await Assert.ThrowsAsync<VncAuthenticationException>(
            () => client.ConnectAsync("127.0.0.1", server.Port, "wrong").WaitAsync(Timeout));
        Assert.False(e.PasswordRequired);
    }

    [Fact]
    public async Task MissingPasswordIsTyped()
    {
        await using var server = new FakeServer(async stream =>
        {
            await Write(stream, new Wire().Bytes(Encoding.ASCII.GetBytes("RFB 003.008\n")));
            await ReadVersion(stream);
            await Write(stream, new Wire().U8(1).U8(2));
        });
        await using var client = new VncClient();
        var e = await Assert.ThrowsAsync<VncAuthenticationException>(
            () => client.ConnectAsync("127.0.0.1", server.Port, null).WaitAsync(Timeout));
        Assert.True(e.PasswordRequired);
    }

    [Fact]
    public async Task SecondViewerIsTurnedAway()
    {
        // V9XVNC.EXE's vnc_turn_away: a 3.3 greeting, security 0, and a reason.
        const string reason = "Another VNC viewer is connected to this machine.";
        await using var server = new FakeServer(async stream =>
        {
            var text = Encoding.ASCII.GetBytes(reason);
            await Write(stream, new Wire().Bytes(Encoding.ASCII.GetBytes("RFB 003.003\n")).U32(0).U32((uint)text.Length).Bytes(text));
            await ReadVersion(stream);
        });
        await using var client = new VncClient();
        var e = await Assert.ThrowsAsync<VncViewerBusyException>(
            () => client.ConnectAsync("127.0.0.1", server.Port, null).WaitAsync(Timeout));
        Assert.Equal(reason, e.Reason);
    }

    [Fact]
    public async Task DesktopSizeResizesAndAsksForAFullUpdate()
    {
        var request = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async stream =>
        {
            await Handshake38(stream, null);
            await ExpectSetup(stream);
            await Write(stream, new Wire().U8(0).U8(0).U16(1).Rect(0, 0, 8, 6, VncEncoding.DesktopSize));
            request.SetResult(await Read(stream, 10));
        });
        await using var client = new VncClient();
        var resized = new TaskCompletionSource<(int, int)>();
        client.Framebuffer.Resized += (w, h) => { if (w == 8) resized.TrySetResult((w, h)); };
        await client.ConnectAsync("127.0.0.1", server.Port, null).WaitAsync(Timeout);
        Assert.Equal((8, 6), await resized.Task.WaitAsync(Timeout));
        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0, 0, 8, 0, 6 }, await request.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task CtrlAltDelIsSixKeyEvents()
    {
        var keys = new TaskCompletionSource<byte[]>();
        await using var server = new FakeServer(async stream =>
        {
            await Handshake38(stream, null);
            await ExpectSetup(stream);
            keys.SetResult(await Read(stream, 48));
        });
        await using var client = new VncClient();
        await client.ConnectAsync("127.0.0.1", server.Port, null).WaitAsync(Timeout);
        await client.SendCtrlAltDelAsync();
        var bytes = await keys.Task.WaitAsync(Timeout);
        var events = Enumerable.Range(0, 6)
            .Select(i => (Down: bytes[i * 8 + 1] == 1, Keysym: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 8 + 4))))
            .ToArray();
        Assert.Equal(
            new[] { (true, 0xFFE3u), (true, 0xFFE9u), (true, 0xFFFFu), (false, 0xFFFFu), (false, 0xFFE9u), (false, 0xFFE3u) },
            events);
    }
}
