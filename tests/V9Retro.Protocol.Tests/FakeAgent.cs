using System.Net;
using System.Net.Sockets;
using V9Retro.Protocol;
using V9Retro.Protocol.Wire;

namespace V9Retro.Protocol.Tests;

/// <summary>A loopback stand-in for V9XAGNT.EXE: frames real protocol bytes so the client is
/// exercised end to end. Handlers map a request type to a reply.</summary>
sealed class FakeAgent : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Task _accept;
    int _active;

    public delegate Task Handler(Responder respond, Frame request);

    public Dictionary<ushort, Handler> Handlers { get; } = new();
    public Capabilities Capabilities { get; set; } = (Capabilities)0x003FFFFF;
    public int MaxConnections { get; set; } = 4;
    public uint BootCounter { get; set; } = 7;
    public List<Frame> Received { get; } = new();

    public FakeAgent()
    {
        _listener.Start();
        _accept = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_stop.Token); }
            catch { return; }
            if (Interlocked.Increment(ref _active) > MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                socket.Dispose(); // the real agent accepts then closes a fifth connection
                continue;
            }
            _ = Serve(socket);
        }
    }

    async Task Serve(Socket socket)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        var respond = new Responder(stream);
        try
        {
            var header = new byte[FrameHeader.Size];
            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                var h = FrameHeader.Read(header);
                var payload = new byte[h.PayloadLength];
                await stream.ReadExactlyAsync(payload, _stop.Token);
                var frame = new Frame(h, payload);
                lock (Received) Received.Add(frame);
                respond.RequestId = h.RequestId;
                if (h.Type == MessageType.HelloRequest)
                    await respond.Send(MessageType.HelloResponse, HelloPayload(Capabilities, BootCounter));
                else if (Handlers.TryGetValue(h.Type, out var handler))
                    await handler(respond, frame);
                else
                    await respond.Error(V9xStatus.UnsupportedOperation, 0, "unknown operation");
            }
        }
        catch
        {
            // client went away
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public static byte[] HelloPayload(Capabilities caps, uint boot = 7) => new PayloadWriter()
        .U16(0x0100).U16(0).U32((uint)caps).U32(65536).U32(boot).U16(9869).U16(0x0202)
        .String("v9x-0.13.0-test")
        .String("")                         // pending job
        .U8(1).U8(0).U8(0).U8(0)            // desktop ready + reserved
        .U32(800).U32(600).U32(16)
        .String("0.0.0.0").String("")
        .ToArray();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; } catch { }
    }

    public sealed class Responder(NetworkStream stream)
    {
        readonly SemaphoreSlim _lock = new(1, 1);
        public uint RequestId { get; set; }

        public async Task Send(ushort type, byte[] payload, uint? requestId = null)
        {
            var buffer = new byte[FrameHeader.Size + payload.Length];
            new FrameHeader(type, requestId ?? RequestId, 0, payload.Length).Write(buffer);
            payload.CopyTo(buffer, FrameHeader.Size);
            await _lock.WaitAsync();
            try { await stream.WriteAsync(buffer); }
            finally { _lock.Release(); }
        }

        public Task Error(V9xStatus status, uint native, string detail) =>
            Send(MessageType.ErrorResponse, new PayloadWriter().U32((uint)status).U32(native).String(detail).ToArray());
    }
}
