using System.Text;
using V9Retro.Protocol.Imaging;
using V9Retro.Protocol.Wire;

namespace V9Retro.Protocol.Tests;

public class SessionTests
{
    static Task<V9xSession> Connect(FakeAgent agent) =>
        V9xSession.ConnectAsync("127.0.0.1", agent.Port, new V9xSessionOptions { ReadTimeout = TimeSpan.FromSeconds(5) });

    static byte[] InfoPayload(bool withId) =>
        new PayloadWriter().U32(9).U32(1000).U32(0x7FFF).U16(9869).U16(0x202).U32(0)
            .String("0.13.0").String("b").String("RETRO").String("4.10").String("s").String("w").String("c")
            .String("").U8(1).U8(0).U8(0).U8(0).U32(640).U32(480).U32(8).String("").String("")
            .Apply(w => { if (withId) w.String("0b1d6f2a-9d5e-4c1b-8a7e-3f2a1b0c9d8e"); })
            .ToArray();

    [Fact]
    public async Task Info_sends_extended_flag_only_when_capable()
    {
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.InfoRequest] = (r, f) =>
            r.Send(MessageType.InfoResponse, InfoPayload(withId: f.Payload.Length == 4 && f.Payload[0] == 1));

        await using (var session = await Connect(agent))
        {
            var info = await session.InfoAsync();
            Assert.Equal("0b1d6f2a-9d5e-4c1b-8a7e-3f2a1b0c9d8e", info.InstallId);
        }

        agent.Capabilities &= ~Capabilities.InstallId;
        await using (var session = await Connect(agent))
        {
            var info = await session.InfoAsync();
            Assert.Null(info.InstallId);
        }
        var infoRequests = agent.Received.Where(f => f.Type == MessageType.InfoRequest).ToList();
        Assert.Equal(4, infoRequests[0].Payload.Length);
        Assert.Empty(infoRequests[1].Payload);
    }

    [Fact]
    public async Task Error_frame_becomes_guest_exception_and_session_stays_usable()
    {
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.RebootRequest] = (r, _) => r.Error(V9xStatus.Busy, 0, "execution slot active");
        agent.Handlers[MessageType.PingRequest] = (r, _) => r.Send(MessageType.PingResponse, new PayloadWriter().U32(5).U32(7).ToArray());
        await using var session = await Connect(agent);
        var ex = await Assert.ThrowsAsync<V9xGuestException>(() => session.RebootAsync("tok1"));
        Assert.Equal(V9xStatus.Busy, ex.Status);
        Assert.Equal("execution slot active", ex.Detail);
        Assert.Equal(7u, (await session.PingAsync()).BootCounter);
    }

    [Fact]
    public async Task Stale_request_ids_are_skipped()
    {
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.PingRequest] = async (r, f) =>
        {
            await r.Send(MessageType.PingResponse, new PayloadWriter().U32(1).U32(1).ToArray(), f.RequestId + 500);
            await r.Send(MessageType.PingResponse, new PayloadWriter().U32(2).U32(2).ToArray());
        };
        await using var session = await Connect(agent);
        Assert.Equal(2u, (await session.PingAsync()).UptimeMs);
    }

    [Fact]
    public async Task Unsupported_capability_fails_before_sending()
    {
        await using var agent = new FakeAgent { Capabilities = Capabilities.Info | Capabilities.Ping };
        await using var session = await Connect(agent);
        await Assert.ThrowsAsync<V9xUnsupportedException>(() => session.ProcessListAsync());
        Assert.DoesNotContain(agent.Received, f => f.Type == MessageType.ProcessListRequest);
    }

    [Fact]
    public async Task Fifth_connection_reports_busy_slots()
    {
        await using var agent = new FakeAgent { MaxConnections = 0 };
        var ex = await Assert.ThrowsAsync<V9xTransportException>(() => Connect(agent));
        Assert.Contains("connection slots", ex.Message);
    }

    [Fact]
    public async Task Screenshot_downloads_and_verifies_the_bmp()
    {
        var bmp = BmpDecoderTests.Sample24();
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.ScreenshotRequest] = (r, f) =>
        {
            // path + 24-byte options block, since the fake advertises SCREENSHOT_OPTIONS
            Assert.Equal(2 + V9xSession.DefaultScreenshotPath.Length + 24, f.Payload.Length);
            return r.Send(MessageType.ScreenshotResponse, new PayloadWriter().U32(2).U32(2).U32(16)
                .U32((uint)bmp.Length).U32(Crc32.Compute(bmp)).String(@"C:\V9XREMOTE\TEMP\SCREEN.BMP").ToArray());
        };
        agent.Handlers[MessageType.FileOpenRead] = async (r, _) =>
        {
            await r.Send(MessageType.FileReadChunk, new PayloadWriter().U32(0).Bytes(bmp.AsSpan(0, 30)).ToArray());
            await r.Send(MessageType.FileReadChunk, new PayloadWriter().U32(30).Bytes(bmp.AsSpan(30)).ToArray());
            await r.Send(MessageType.FileReadComplete, new PayloadWriter().U32((uint)bmp.Length).U32(Crc32.Compute(bmp)).ToArray());
        };
        await using var session = await Connect(agent);
        var (info, data) = await session.ScreenshotAsync(new ScreenshotOptions());
        Assert.Equal(2u, info.Width);
        Assert.Equal(bmp, data);
        Assert.Equal(2, BmpDecoder.Decode(data).Height);
    }

    [Fact]
    public async Task Download_rejects_crc_mismatch()
    {
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.FileOpenRead] = async (r, _) =>
        {
            await r.Send(MessageType.FileReadChunk, new PayloadWriter().U32(0).Bytes("abc"u8).ToArray());
            await r.Send(MessageType.FileReadComplete, new PayloadWriter().U32(3).U32(12345).ToArray());
        };
        await using var session = await Connect(agent);
        await Assert.ThrowsAsync<V9xProtocolException>(() => session.DownloadBytesAsync(@"C:\X.TXT"));
    }

    [Fact]
    public async Task Upload_sends_chunks_and_commits()
    {
        var data = new byte[V9xSession.ChunkSize + 100];
        new Random(1).NextBytes(data);
        var received = new MemoryStream();
        uint expectedCrc = 0, expectedSize = 0;
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.FileOpenWrite] = (r, f) =>
        {
            expectedSize = BitConverter.ToUInt32(f.Payload, 0);
            expectedCrc = BitConverter.ToUInt32(f.Payload, 4);
            return r.Send(MessageType.FileWriteReady, f.Payload.AsSpan(0, 8).ToArray());
        };
        agent.Handlers[MessageType.FileWriteChunk] = (r, f) =>
        {
            Assert.Equal((uint)received.Length, BitConverter.ToUInt32(f.Payload, 0));
            received.Write(f.Payload, 4, f.Payload.Length - 4);
            return r.Send(MessageType.FileWriteAck, new PayloadWriter().U32((uint)received.Length).ToArray());
        };
        agent.Handlers[MessageType.FileCommit] = (r, _) =>
            r.Send(MessageType.FileWriteComplete, new PayloadWriter().U32((uint)received.Length).U32(Crc32.Compute(received.ToArray())).ToArray());

        await using var session = await Connect(agent);
        var result = await session.UploadBytesAsync(data, @"C:\V9XREMOTE\X.BIN");
        Assert.Equal(data, received.ToArray());
        Assert.Equal(Crc32.Compute(data), result.Crc32);
        Assert.Equal(expectedCrc, result.Crc32);
        Assert.Equal((uint)data.Length, expectedSize);
    }

    static byte[] Complete(ExecResultCategory result, ExecFlags flags) =>
        new PayloadWriter().U32((uint)result).S32(0).U32(0).U32(12).U32(5).U32(0).U32((uint)flags).ToArray();

    [Fact]
    public async Task Exec_streams_output_then_completes()
    {
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.ExecRequest] = async (r, _) =>
        {
            await r.Send(MessageType.ExecAccepted, new PayloadWriter().U32(0).ToArray());
            await r.Send(MessageType.ExecStdout, Encoding.ASCII.GetBytes("Hel"));
            await r.Send(MessageType.ExecStdout, Encoding.ASCII.GetBytes("lo"));
            await r.Send(MessageType.ExecStderr, Encoding.ASCII.GetBytes("!"));
            await r.Send(MessageType.ExecComplete, Complete(ExecResultCategory.Ok, ExecFlags.PipeCapture));
        };
        await using var session = await Connect(agent);
        var (result, stdout, stderr) = await session.ExecAsync(new ExecRequest { Arguments = "echo Hello" });
        Assert.Equal("Hello", stdout);
        Assert.Equal("!", stderr);
        Assert.Equal(ExecResultCategory.Ok, result.Result);
    }

    [Fact]
    public async Task Exec_cancel_sends_cancel_and_waits_for_completion()
    {
        await using var agent = new FakeAgent();
        uint execId = 0;
        agent.Handlers[MessageType.ExecRequest] = async (r, f) =>
        {
            execId = f.RequestId;
            await r.Send(MessageType.ExecAccepted, new PayloadWriter().U32(0).ToArray());
            await r.Send(MessageType.ExecStdout, Encoding.ASCII.GetBytes("working"));
        };
        agent.Handlers[MessageType.CancelRequest] = async (r, f) =>
        {
            Assert.Equal(execId, BitConverter.ToUInt32(f.Payload));
            await r.Send(MessageType.CancelResponse, new PayloadWriter().U32(execId).U32(1).ToArray());
            await r.Send(MessageType.ExecComplete, Complete(ExecResultCategory.Cancelled, ExecFlags.Cancelled), execId);
        };
        await using var session = await Connect(agent);
        using var cts = new CancellationTokenSource();
        ExecResult? final = null;
        await foreach (var e in session.ExecStreamAsync(new ExecRequest { Arguments = "pause" }, cts.Token))
        {
            if (e is ExecEvent.Stdout) cts.Cancel();
            if (e is ExecEvent.Completed c) final = c.Result;
        }
        Assert.Equal(ExecResultCategory.Cancelled, final?.Result);
        Assert.Contains(agent.Received, f => f.Type == MessageType.CancelRequest);
    }

    [Fact]
    public async Task Power_token_is_checked()
    {
        await using var agent = new FakeAgent();
        agent.Handlers[MessageType.RebootRequest] = (r, f) => r.Send(MessageType.RebootAccepted,
            new PayloadWriter().U32(8).Bytes(f.Payload).ToArray());
        await using var session = await Connect(agent);
        var token = V9xSession.NewPowerToken();
        var accepted = await session.RebootAsync(token);
        Assert.Equal(token, accepted.Token);
        Assert.Equal(8u, accepted.BootCounter);
        Assert.InRange(token.Length, 1, 63);
    }
}

static class PayloadWriterTestExtensions
{
    public static PayloadWriter Apply(this PayloadWriter w, Action<PayloadWriter> body)
    {
        body(w);
        return w;
    }
}
