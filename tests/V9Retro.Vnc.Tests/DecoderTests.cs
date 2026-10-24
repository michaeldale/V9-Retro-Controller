using System.IO.Compression;
using V9Retro.Vnc.Decoding;

namespace V9Retro.Vnc.Tests;

public class DecoderTests
{
    private const uint Red = 0xFF0000, Green = 0x00FF00, Blue = 0x0000FF, White = 0xFFFFFF, Grey = 0x808080, Black = 0;

    private static RfbReader Reader(Wire wire) => new(new MemoryStream(wire.ToArray()));

    private static uint Opaque(uint rgb) => rgb | 0xFF000000u;

    private static void AssertPixel(Framebuffer fb, int x, int y, uint rgb) =>
        Assert.True(fb.GetPixel(x, y) == Opaque(rgb), $"pixel {x},{y} is {fb.GetPixel(x, y):X8}, expected {Opaque(rgb):X8}");

    private static void AssertFullyRead(RfbReader reader) =>
        Assert.Equal(-1, reader.BaseStream.ReadByte());

    [Fact]
    public void RawWritesPixelsRowByRow()
    {
        var fb = new Framebuffer(4, 4);
        var reader = Reader(new Wire().Pixel(Red).Pixel(Green).Pixel(Blue).Pixel(White));
        RawDecoder.Decode(reader, fb, new VncRect(1, 2, 2, 2));
        AssertPixel(fb, 1, 2, Red);
        AssertPixel(fb, 2, 2, Green);
        AssertPixel(fb, 1, 3, Blue);
        AssertPixel(fb, 2, 3, White);
        AssertPixel(fb, 0, 0, Black);
        AssertFullyRead(reader);
    }

    [Fact]
    public void FramebufferBytesAreBgra()
    {
        var fb = new Framebuffer(1, 1);
        RawDecoder.Decode(Reader(new Wire().Pixel(0x112233)), fb, new VncRect(0, 0, 1, 1));
        using var access = fb.Lock();
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0xFF }, access.Pixels.ToArray());
        Assert.Equal(4, access.Stride);
    }

    [Fact]
    public void CopyRectHandlesOverlapDownAndRight()
    {
        var fb = new Framebuffer(4, 4);
        var wire = new Wire();
        for (int i = 0; i < 16; i++) wire.Pixel((uint)i);
        RawDecoder.Decode(Reader(wire), fb, new VncRect(0, 0, 4, 4));

        // Move the 3x3 block at 0,0 to 1,1: source and destination overlap.
        var reader = Reader(new Wire().U16(0).U16(0));
        CopyRectDecoder.Decode(reader, fb, new VncRect(1, 1, 3, 3));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                AssertPixel(fb, x + 1, y + 1, (uint)(y * 4 + x));
        AssertPixel(fb, 0, 0, 0);
        AssertFullyRead(reader);
    }

    [Fact]
    public void CopyRectHandlesOverlapUpAndLeft()
    {
        var fb = new Framebuffer(4, 4);
        var wire = new Wire();
        for (int i = 0; i < 16; i++) wire.Pixel((uint)i);
        RawDecoder.Decode(Reader(wire), fb, new VncRect(0, 0, 4, 4));

        CopyRectDecoder.Decode(Reader(new Wire().U16(1).U16(1)), fb, new VncRect(0, 0, 3, 3));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                AssertPixel(fb, x, y, (uint)((y + 1) * 4 + x + 1));
    }

    [Fact]
    public void HextileCoversEverySubencodingFlag()
    {
        // A 20x18 rect: tiles 16x16, 4x16, 16x2, 4x2.
        var fb = new Framebuffer(24, 20);
        var wire = new Wire();
        // Tile 0 (16x16): background + foreground, two mono subrects.
        wire.U8(HextileDecoder.BackgroundSpecified | HextileDecoder.ForegroundSpecified | HextileDecoder.AnySubrects)
            .Pixel(Grey).Pixel(Red).U8(2)
            .U8(0x00).U8(0x10)  // at 0,0, 2x1
            .U8(0xFF).U8(0x00); // at 15,15, 1x1
        // Tile 1 (4x16): background carried over, one coloured subrect.
        wire.U8(HextileDecoder.AnySubrects | HextileDecoder.SubrectsColoured)
            .U8(1).Pixel(Blue).U8(0x12).U8(0x21); // at 1,2, 3x2
        // Tile 2 (16x2): raw.
        wire.U8(HextileDecoder.Raw);
        for (int i = 0; i < 32; i++) wire.Pixel((uint)i);
        // Tile 3 (4x2): background only, carried foreground reused by a mono subrect.
        wire.U8(HextileDecoder.BackgroundSpecified | HextileDecoder.AnySubrects)
            .Pixel(White).U8(1).U8(0x31).U8(0x00); // at 3,1, 1x1 in foreground (red)

        var reader = Reader(wire);
        HextileDecoder.Decode(reader, fb, new VncRect(2, 1, 20, 18));
        AssertFullyRead(reader);

        int ox = 2, oy = 1;
        AssertPixel(fb, ox + 0, oy + 0, Red);
        AssertPixel(fb, ox + 1, oy + 0, Red);
        AssertPixel(fb, ox + 2, oy + 0, Grey);
        AssertPixel(fb, ox + 15, oy + 15, Red);
        AssertPixel(fb, ox + 7, oy + 7, Grey);

        AssertPixel(fb, ox + 16, oy + 0, Grey);
        AssertPixel(fb, ox + 17, oy + 2, Blue);
        AssertPixel(fb, ox + 19, oy + 3, Blue);
        AssertPixel(fb, ox + 16, oy + 3, Grey);

        AssertPixel(fb, ox + 0, oy + 16, 0);
        AssertPixel(fb, ox + 15, oy + 16, 15);
        AssertPixel(fb, ox + 0, oy + 17, 16);

        AssertPixel(fb, ox + 16, oy + 16, White);
        AssertPixel(fb, ox + 19, oy + 17, Red);
        AssertPixel(fb, 0, 0, Black);
    }

    [Fact]
    public void HextileRejectsSubrectOutsideTile()
    {
        var fb = new Framebuffer(4, 4);
        var wire = new Wire().U8(HextileDecoder.BackgroundSpecified | HextileDecoder.AnySubrects | HextileDecoder.ForegroundSpecified)
            .Pixel(Grey).Pixel(Red).U8(1).U8(0x33).U8(0x11);
        Assert.Throws<VncProtocolException>(() => HextileDecoder.Decode(Reader(wire), fb, new VncRect(0, 0, 4, 4)));
    }

    /// <summary>Wraps the uncompressed ZRLE data of one rect the way the server does.</summary>
    private static Wire ZrleRect(Func<byte[], byte[]> compress, Wire raw)
    {
        var packed = compress(raw.ToArray());
        return new Wire().U32((uint)packed.Length).Bytes(packed);
    }

    public static TheoryData<string> Compressors => ["server", "zlib"];

    private static Func<byte[], byte[]> Compressor(string kind)
    {
        if (kind == "server")
        {
            var deflate = new FixedDeflate();
            return deflate.Sync;
        }
        var sink = new MemoryStream();
        var zlib = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);
        return data =>
        {
            sink.SetLength(0);
            zlib.Write(data);
            zlib.Flush(); // sync flush, as ZRLE requires
            return sink.ToArray();
        };
    }

    [Theory]
    [MemberData(nameof(Compressors))]
    public void ZrleDecodesEveryTileTypeAcrossOneStream(string kind)
    {
        var compress = Compressor(kind);
        var fb = new Framebuffer(80, 8);
        var zrle = new ZrleDecoder();

        // Rect 1: 70x4, so two tiles (64x4 and 6x4). Tile A: palette RLE; tile B: raw.
        var raw = new Wire();
        raw.U8(128 + 3).CPixel(Red).CPixel(Green).CPixel(Blue);
        raw.U8(0 | 128).U8(99);  // red x 100
        raw.U8(1);                // green x 1
        raw.U8(2 | 128).U8(154); // blue x 155 -> 256 total
        raw.U8(0);
        for (int i = 0; i < 24; i++) raw.CPixel((uint)(i * 0x010101));
        var reader = Reader(ZrleRect(compress, raw));
        zrle.Decode(reader, fb, new VncRect(0, 0, 70, 4));
        AssertFullyRead(reader);
        AssertPixel(fb, 0, 0, Red);
        AssertPixel(fb, 35, 1, Red);   // index 99
        AssertPixel(fb, 36, 1, Green); // index 100
        AssertPixel(fb, 37, 1, Blue);
        AssertPixel(fb, 63, 3, Blue);
        AssertPixel(fb, 64, 0, 0);
        AssertPixel(fb, 69, 3, 23 * 0x010101);

        // Every later rect continues the same zlib stream. Solid tile.
        reader = Reader(ZrleRect(compress, new Wire().U8(1).CPixel(Grey)));
        zrle.Decode(reader, fb, new VncRect(70, 0, 10, 8));
        AssertFullyRead(reader);
        AssertPixel(fb, 70, 0, Grey);
        AssertPixel(fb, 79, 7, Grey);

        // Packed palette, 2 colours (1 bit): 10 pixels per row -> 2 bytes per row.
        raw = new Wire().U8(2).CPixel(Black).CPixel(White)
            .U8(0b1010_1010).U8(0b1100_0000)  // row 0: W B W B W B W B | W W
            .U8(0b0000_0000).U8(0b0100_0000); // row 1: all black except column 9
        reader = Reader(ZrleRect(compress, raw));
        zrle.Decode(reader, fb, new VncRect(0, 4, 10, 2));
        AssertFullyRead(reader);
        AssertPixel(fb, 0, 4, White);
        AssertPixel(fb, 1, 4, Black);
        AssertPixel(fb, 8, 4, White);
        AssertPixel(fb, 9, 4, White);
        AssertPixel(fb, 8, 5, Black);
        AssertPixel(fb, 9, 5, White);

        // Packed palette, 3 colours (2 bits): 5 pixels per row -> 2 bytes per row.
        raw = new Wire().U8(3).CPixel(Red).CPixel(Green).CPixel(Blue)
            .U8(0b00_01_10_00).U8(0b01_000000);
        reader = Reader(ZrleRect(compress, raw));
        zrle.Decode(reader, fb, new VncRect(10, 4, 5, 1));
        AssertFullyRead(reader);
        AssertPixel(fb, 10, 4, Red);
        AssertPixel(fb, 11, 4, Green);
        AssertPixel(fb, 12, 4, Blue);
        AssertPixel(fb, 13, 4, Red);
        AssertPixel(fb, 14, 4, Green);

        // Packed palette, 5 colours (4 bits): 3 pixels per row -> 2 bytes per row.
        raw = new Wire().U8(5).CPixel(Red).CPixel(Green).CPixel(Blue).CPixel(White).CPixel(Grey)
            .U8(0x43).U8(0x20);
        reader = Reader(ZrleRect(compress, raw));
        zrle.Decode(reader, fb, new VncRect(15, 4, 3, 1));
        AssertFullyRead(reader);
        AssertPixel(fb, 15, 4, Grey);
        AssertPixel(fb, 16, 4, White);
        AssertPixel(fb, 17, 4, Blue);

        // Plain RLE with a run longer than 255 (needs a continuation byte): 40x8 = 320 pixels.
        raw = new Wire().U8(128)
            .CPixel(Blue).U8(255).U8(4)   // 1 + 255 + 4 = 260
            .CPixel(Red).U8(59);          // 60
        reader = Reader(ZrleRect(compress, raw));
        zrle.Decode(reader, fb, new VncRect(30, 0, 40, 8));
        AssertFullyRead(reader);
        AssertPixel(fb, 30, 0, Blue);
        AssertPixel(fb, 49, 6, Blue);  // index 259
        AssertPixel(fb, 50, 6, Red);   // index 260
        AssertPixel(fb, 69, 7, Red);
    }

    [Fact]
    public void ZrlePlainRleLongRuns()
    {
        var compress = Compressor("server");
        var fb = new Framebuffer(20, 16);
        var raw = new Wire().U8(128)
            .CPixel(Blue).U8(255).U8(4) // 1 + 255 + 4 = 260
            .CPixel(Red).U8(59);        // 60, 320 in all
        var reader = Reader(ZrleRect(compress, raw));
        new ZrleDecoder().Decode(reader, fb, new VncRect(0, 0, 20, 16));
        AssertFullyRead(reader);
        AssertPixel(fb, 0, 0, Blue);
        AssertPixel(fb, 19, 12, Blue);  // index 259
        AssertPixel(fb, 0, 13, Red);    // index 260
        AssertPixel(fb, 19, 15, Red);
    }

    [Fact]
    public void ZrleShortRectIsAProtocolError()
    {
        var compress = Compressor("server");
        var fb = new Framebuffer(4, 4);
        var reader = Reader(ZrleRect(compress, new Wire().U8(0).CPixel(Red))); // raw tile needs 16 pixels
        Assert.Throws<VncProtocolException>(() => new ZrleDecoder().Decode(reader, fb, new VncRect(0, 0, 4, 4)));
    }

    [Fact]
    public void RichCursorAppliesTheMask()
    {
        // 2x2 cursor, hotspot 1,0: top-left and bottom-right opaque.
        var wire = new Wire().Pixel(Red).Pixel(Green).Pixel(Blue).Pixel(White)
            .U8(0b1000_0000).U8(0b0100_0000);
        var shape = VncClient.ReadRichCursor(Reader(wire), new VncRect(1, 0, 2, 2));
        Assert.Equal(new VncPoint(1, 0), shape.Hotspot);
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0xFF, 0xFF, 0, 0, 0, 0,
            0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF,
        }, shape.Bgra);
    }
}
