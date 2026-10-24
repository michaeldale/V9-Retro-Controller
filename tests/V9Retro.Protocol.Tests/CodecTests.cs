using V9Retro.Protocol.Wire;

namespace V9Retro.Protocol.Tests;

public class CodecTests
{
    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        var running = Crc32.Append(Crc32.Append(0, "1234"u8), "56789"u8);
        Assert.Equal(0xCBF43926u, running);
    }

    [Fact]
    public void Header_golden_bytes()
    {
        var buffer = new byte[FrameHeader.Size];
        new FrameHeader(MessageType.HelloRequest, 1, 0, 13).Write(buffer);
        Assert.Equal("56395852" + "0001" + "0100" + "01000000" + "00000000" + "0D000000" + "00000000",
            Convert.ToHexString(buffer));
        var back = FrameHeader.Read(buffer);
        Assert.Equal(new FrameHeader(MessageType.HelloRequest, 1, 0, 13), back);
    }

    [Fact]
    public void Header_rejects_bad_magic_oversize_and_reserved()
    {
        var buffer = new byte[FrameHeader.Size];
        new FrameHeader(1, 1, 0, 0).Write(buffer);
        var bad = (byte[])buffer.Clone();
        bad[0] = (byte)'X';
        Assert.Throws<V9xProtocolException>(() => FrameHeader.Read(bad));
        bad = (byte[])buffer.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan(16), 65537u);
        Assert.Throws<V9xProtocolException>(() => FrameHeader.Read(bad));
        bad = (byte[])buffer.Clone();
        bad[20] = 1;
        Assert.Throws<V9xProtocolException>(() => FrameHeader.Read(bad));
    }

    [Fact]
    public void Strings_are_u16_length_ascii_and_reject_non_ascii()
    {
        Assert.Equal("0300414243", Convert.ToHexString(new PayloadWriter().String("ABC").ToArray()));
        Assert.Throws<ArgumentException>(() => new PayloadWriter().String("caf\u00e9"));
        Assert.Throws<ArgumentException>(() => new PayloadWriter().String("a\0b"));
    }

    [Fact]
    public void Hello_parses_full_and_short_responses()
    {
        var hello = Parsers.ParseHello(FakeAgent.HelloPayload(Capabilities.Info | Capabilities.InstallId));
        Assert.Equal(0x0100, hello.ProtocolVersion);
        Assert.Equal("v9x-0.13.0-test", hello.BuildId);
        Assert.True(hello.Desktop.DesktopReady);
        Assert.Equal(800u, hello.Desktop.ScreenWidth);
        Assert.Equal("0.0.0.0", hello.Desktop.ListenAddress);

        // An M1-era agent stops after the build ID.
        var old = new PayloadWriter().U16(0x100).U16(0).U32(1).U32(65536).U32(3).U16(9869).U16(0x101).String("old").ToArray();
        var parsed = Parsers.ParseHello(old);
        Assert.Equal(DesktopBlock.Empty, parsed.Desktop);
    }

    static PayloadWriter InfoBase() => new PayloadWriter()
        .U32(42).U32(123456).U32(0x6001).U16(9869).U16(0x0202).U32(0xC0000A04)
        .String("0.13.0").String("build-1").String("RETRO98").String("4.10.2222 A")
        .String(@"C:\WINDOWS\SYSTEM").String(@"C:\WINDOWS").String(@"C:\V9XREMOTE");

    [Fact]
    public void Info_parses_old_short_response()
    {
        var info = Parsers.ParseInfo(InfoBase().ToArray(), extended: false);
        Assert.Equal("RETRO98", info.ComputerName);
        Assert.Equal(42u, info.BootCounter);
        Assert.Null(info.InstallId);
    }

    [Fact]
    public void Info_parses_extended_install_id()
    {
        var payload = InfoBase().String("bt0123").U8(1).U8(0).U8(0).U8(0).U32(1024).U32(768).U32(32)
            .String("192.168.1.5").String("192.168.1.2").String("6F9619FF-8B86-4D11-B42D-00C04FC964FF").ToArray();
        var info = Parsers.ParseInfo(payload, extended: true);
        Assert.Equal("bt0123", info.Desktop.PendingJob);
        Assert.Equal(1024u, info.Desktop.ScreenWidth);
        Assert.Equal("192.168.1.2", info.Desktop.AllowedClient);
        Assert.Equal("6f9619ff-8b86-4d11-b42d-00c04fc964ff", info.InstallId);
    }

    [Fact]
    public void Info_rejects_trailing_data_when_not_extended()
    {
        var payload = InfoBase().String("").U8(0).U8(0).U8(0).U8(0).U32(0).U32(0).U32(0)
            .String("").String("").String("unexpected").ToArray();
        Assert.Throws<V9xProtocolException>(() => Parsers.ParseInfo(payload, extended: false));
    }

    [Fact]
    public void Inventory_keeps_repeated_keys()
    {
        var payload = new PayloadWriter().U16(2)
            .String("net").U16(3).String("hostname").String("retro").String("ip").String("10.0.0.5").String("ip").String("10.0.0.6")
            .String("meta").U16(1).String("install_id").String("abc")
            .ToArray();
        var records = Parsers.ParseInventory(payload);
        Assert.Equal(2, records.Count);
        Assert.Equal(["10.0.0.5", "10.0.0.6"], records[0].GetAll("ip"));
        Assert.Equal("abc", records[1].Get("install_id"));
    }

    [Fact]
    public void Process_window_and_registry_lists_parse()
    {
        var procs = Parsers.ParseProcessList(new PayloadWriter().U32(1).U32(0xFFF1).U32(0).U32(3).U32(1).U32(9)
            .String(@"C:\WINDOWS\NOTEPAD.EXE").ToArray());
        Assert.Equal("NOTEPAD.EXE", procs[0].Name);
        Assert.True(procs[0].IsAgentJob);

        var windows = Parsers.ParseWindowList(new PayloadWriter().U32(1).U32(0).U32(0x10).U32(5).U32(1)
            .S32(10).S32(20).S32(110).S32(220).String("Notepad").String("Untitled - Notepad").ToArray());
        Assert.Equal(100, windows.Windows[0].Width);
        Assert.True(windows.Windows[0].IsForeground);

        var reg = Parsers.ParseRegQuery(new PayloadWriter().U32(1).U32(2).U32(0).String("Software")
            .String("").U32(1).U32(4).Bytes("abc\0"u8)
            .String("Count").U32(4).U32(4).U32(7).ToArray());
        Assert.Equal("Software", reg.Subkeys[0]);
        Assert.Equal("abc", reg.Values[0].DisplayData);
        Assert.Equal("0x00000007 (7)", reg.Values[1].DisplayData);
    }

    [Fact]
    public void Input_actions_encode_per_opcode()
    {
        var w = new PayloadWriter();
        new InputAction.MoveTo(1, -1).Write(w);
        new InputAction.Button(MouseButton.Right, true).Write(w);
        new InputAction.Wheel(-2).Write(w);
        new InputAction.Key(0x1B, false).Write(w);
        new InputAction.TypeText("hi").Write(w);
        new InputAction.Delay(50).Write(w);
        new InputAction.MoveBy(3, 4).Write(w);
        Assert.Equal(
            "01" + "01000000" + "FFFFFFFF" +
            "02" + "01" + "01" +
            "03" + "FEFF" +
            "04" + "1B" + "00" +
            "05" + "0200" + "6869" +
            "06" + "3200" +
            "07" + "03000000" + "04000000",
            Convert.ToHexString(w.ToArray()));
    }

    [Fact]
    public void Version_parsing_and_update_gate()
    {
        Assert.Equal(new Version(0, 13, 0), AgentFeatures.ParseVersion("0.13.0"));
        Assert.Equal(new Version(0, 12, 1), AgentFeatures.ParseVersion("0.12.1-dev"));
        Assert.Null(AgentFeatures.ParseVersion("garbage"));
        var caps = Capabilities.DriverUpdate | Capabilities.FileWrite;
        Assert.False(AgentFeatures.CanHotUpdate(caps, "0.5.9"));
        Assert.True(AgentFeatures.CanHotUpdate(caps, "0.6.0"));
    }
}
