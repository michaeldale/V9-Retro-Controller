using System.Buffers.Binary;

namespace V9Retro.Protocol.Imaging;

/// <summary>A decoded image: top-down rows of B, G, R, A bytes (A always 255).</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>Decodes what the agent's screenshot helper writes: 24-bit BI_RGB, and from 0.13.0
/// 8-bit BI_RGB or BI_RLE8 with a palette (src\common\shotxf.c). Also accepts 16/32-bit BI_RGB.
/// No System.Drawing, so it runs on every platform.</summary>
public static class BmpDecoder
{
    public static BgraImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
            throw new V9xProtocolException("not a BMP file");
        var pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data[30..]);
        var colours = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[46..]);
        if (width <= 0 || rawHeight == 0 || width > 8192 || Math.Abs(rawHeight) > 8192)
            throw new V9xProtocolException("invalid BMP dimensions");
        if (pixelOffset > data.Length) throw new V9xProtocolException("truncated BMP");
        var bottomUp = rawHeight > 0;
        var height = Math.Abs(rawHeight);
        var output = new byte[width * height * 4];

        // Stored row y lands at output row (bottomUp ? height-1-y : y).
        int OutRow(int y) => (bottomUp ? height - 1 - y : y) * width * 4;

        switch (bpp, compression)
        {
            case (24, 0):
            {
                var stride = (width * 3 + 3) & ~3;
                if (pixelOffset + (long)stride * height > data.Length) throw new V9xProtocolException("truncated BMP pixel data");
                for (var y = 0; y < height; y++)
                {
                    var src = data.Slice(pixelOffset + y * stride, width * 3);
                    var dst = output.AsSpan(OutRow(y), width * 4);
                    for (int x = 0, s = 0, d = 0; x < width; x++, s += 3, d += 4)
                    {
                        dst[d] = src[s];
                        dst[d + 1] = src[s + 1];
                        dst[d + 2] = src[s + 2];
                        dst[d + 3] = 255;
                    }
                }
                break;
            }
            case (32, 0):
            {
                var stride = width * 4;
                if (pixelOffset + (long)stride * height > data.Length) throw new V9xProtocolException("truncated BMP pixel data");
                for (var y = 0; y < height; y++)
                {
                    var dst = output.AsSpan(OutRow(y), stride);
                    data.Slice(pixelOffset + y * stride, stride).CopyTo(dst);
                    for (var d = 3; d < stride; d += 4) dst[d] = 255;
                }
                break;
            }
            case (16, 0):
            {
                var stride = (width * 2 + 3) & ~3;
                if (pixelOffset + (long)stride * height > data.Length) throw new V9xProtocolException("truncated BMP pixel data");
                for (var y = 0; y < height; y++)
                {
                    var src = data.Slice(pixelOffset + y * stride, width * 2);
                    var dst = output.AsSpan(OutRow(y), width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        int v = BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 2)..]); // X1R5G5B5
                        dst[x * 4] = (byte)((v & 0x1F) * 255 / 31);
                        dst[x * 4 + 1] = (byte)(((v >> 5) & 0x1F) * 255 / 31);
                        dst[x * 4 + 2] = (byte)(((v >> 10) & 0x1F) * 255 / 31);
                        dst[x * 4 + 3] = 255;
                    }
                }
                break;
            }
            case (8, 0 or 1):
            {
                colours = colours == 0 ? 256 : colours;
                var paletteStart = 14 + headerSize;
                if (colours > 256 || paletteStart + colours * 4 > pixelOffset)
                    throw new V9xProtocolException("invalid BMP palette");
                var palette = data.Slice(paletteStart, colours * 4);
                var indices = new byte[width * height];
                if (compression == 0)
                {
                    var stride = (width + 3) & ~3;
                    if (pixelOffset + (long)stride * height > data.Length) throw new V9xProtocolException("truncated BMP pixel data");
                    for (var y = 0; y < height; y++)
                        data.Slice(pixelOffset + y * stride, width).CopyTo(indices.AsSpan(y * width));
                }
                else
                {
                    DecodeRle8(data[pixelOffset..], width, height, indices);
                }
                for (var y = 0; y < height; y++)
                {
                    var dst = output.AsSpan(OutRow(y), width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var i = indices[y * width + x];
                        if (i >= colours) throw new V9xProtocolException("BMP pixel outside the palette");
                        // Pixels an RLE delta skipped keep palette entry 0, as GDI does.
                        dst[x * 4] = palette[i * 4];
                        dst[x * 4 + 1] = palette[i * 4 + 1];
                        dst[x * 4 + 2] = palette[i * 4 + 2];
                        dst[x * 4 + 3] = 255;
                    }
                }
                break;
            }
            default:
                throw new V9xProtocolException($"unsupported BMP format ({bpp}-bit, compression {compression})");
        }
        return new BgraImage(width, height, output);
    }

    static void DecodeRle8(ReadOnlySpan<byte> rle, int width, int height, byte[] indices)
    {
        int at = 0, x = 0, y = 0;
        while (true)
        {
            if (at + 2 > rle.Length) throw new V9xProtocolException("truncated RLE8 data");
            int count = rle[at], value = rle[at + 1];
            at += 2;
            if (count > 0)
            {
                if (y >= height || x + count > width) throw new V9xProtocolException("RLE8 run outside the image");
                indices.AsSpan(y * width + x, count).Fill((byte)value);
                x += count;
            }
            else if (value == 0) { x = 0; y++; }
            else if (value == 1) return;
            else if (value == 2)
            {
                if (at + 2 > rle.Length) throw new V9xProtocolException("truncated RLE8 delta");
                x += rle[at];
                y += rle[at + 1];
                at += 2;
            }
            else
            {
                if (y >= height || x + value > width || at + value > rle.Length)
                    throw new V9xProtocolException("RLE8 literal outside the image");
                rle.Slice(at, value).CopyTo(indices.AsSpan(y * width + x));
                x += value;
                at += value + (value & 1);
            }
        }
    }
}
