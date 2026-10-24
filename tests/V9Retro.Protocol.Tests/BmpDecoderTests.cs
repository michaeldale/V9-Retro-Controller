using V9Retro.Protocol.Imaging;
using V9Retro.Protocol.Wire;

namespace V9Retro.Protocol.Tests;

public class BmpDecoderTests
{
    /// <summary>A BMP file header plus BITMAPINFOHEADER, then palette and pixels.</summary>
    internal static byte[] Bmp(int width, int height, ushort bpp, uint compression, byte[] palette, byte[] pixels)
    {
        var offset = 54 + palette.Length;
        return new PayloadWriter()
            .U8((byte)'B').U8((byte)'M').U32((uint)(offset + pixels.Length)).U32(0).U32((uint)offset)
            .U32(40).S32(width).S32(height).U16(1).U16(bpp).U32(compression).U32((uint)pixels.Length)
            .U32(2835).U32(2835).U32((uint)(palette.Length / 4)).U32(0)
            .Bytes(palette).Bytes(pixels)
            .ToArray();
    }

    /// <summary>2x2, bottom-up: bottom row blue, green; top row red, white.</summary>
    internal static byte[] Sample24() => Bmp(2, 2, 24, 0, [],
    [
        0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, // bottom row, padded to 8
        0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, // top row
    ]);

    [Fact]
    public void Decodes_24bit_bottom_up_to_top_down_bgra()
    {
        var image = BmpDecoder.Decode(Sample24());
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal("0000FFFF" + "FFFFFFFF" + "FF0000FF" + "00FF00FF", Convert.ToHexString(image.Pixels));
    }

    [Fact]
    public void Decodes_rle8_with_palette()
    {
        byte[] palette = [0, 0, 0, 0, /* 1 = red */ 0, 0, 0xFF, 0, /* 2 = blue */ 0xFF, 0, 0, 0];
        byte[] rle =
        [
            4, 1, 0, 0,             // bottom row: 4 x red, end of line
            0, 4, 1, 2, 1, 2, 0, 0, // top row: literal red, blue, red, blue; end of line
            0, 1,                   // end of bitmap
        ];
        var image = BmpDecoder.Decode(Bmp(4, 2, 8, 1, palette, rle));
        var top = Convert.ToHexString(image.Pixels.AsSpan(0, 16));
        var bottom = Convert.ToHexString(image.Pixels.AsSpan(16, 16));
        Assert.Equal("0000FFFF" + "FF0000FF" + "0000FFFF" + "FF0000FF", top);
        Assert.Equal(string.Concat(Enumerable.Repeat("0000FFFF", 4)), bottom);
    }

    [Fact]
    public void Rejects_runs_outside_the_image()
    {
        byte[] palette = [0, 0, 0, 0];
        Assert.Throws<V9xProtocolException>(() => BmpDecoder.Decode(Bmp(2, 1, 8, 1, palette, [5, 0, 0, 1])));
        Assert.Throws<V9xProtocolException>(() => BmpDecoder.Decode([1, 2, 3]));
    }
}
