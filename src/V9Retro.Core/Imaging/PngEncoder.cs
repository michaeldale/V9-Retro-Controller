using System.Buffers.Binary;
using System.IO.Compression;
using V9Retro.Protocol;
using V9Retro.Protocol.Imaging;

namespace V9Retro.Core.Imaging;

/// <summary>Minimal RGB PNG writer with a per-row filter choice (none, sub, up), enough for
/// screenshots of 9x desktops without pulling a graphics library into Core.</summary>
public static class PngEncoder
{
    public static byte[] Encode(BgraImage image, CompressionLevel level = CompressionLevel.Optimal)
    {
        int w = image.Width, h = image.Height, rowBytes = w * 3;
        var prev = new byte[rowBytes];
        var cur = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, level, leaveOpen: true))
        {
            for (var y = 0; y < h; y++)
            {
                var src = image.Pixels.AsSpan(y * w * 4, w * 4);
                for (int x = 0, s = 0, d = 0; x < w; x++, s += 4, d += 3)
                {
                    cur[d] = src[s + 2];
                    cur[d + 1] = src[s + 1];
                    cur[d + 2] = src[s];
                }
                byte bestFilter = 0;
                cur.CopyTo(best, 0);
                var bestScore = Score(cur);
                for (byte filter = 1; filter <= 2; filter++)
                {
                    for (var i = 0; i < rowBytes; i++)
                    {
                        var left = i >= 3 ? cur[i - 3] : 0;
                        candidate[i] = (byte)(cur[i] - (filter == 1 ? left : prev[i]));
                    }
                    var score = Score(candidate);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestFilter = filter;
                        candidate.CopyTo(best, 0);
                    }
                }
                z.WriteByte(bestFilter);
                z.Write(best);
                (prev, cur) = (cur, prev);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolour RGB
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, raw.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    static long Score(ReadOnlySpan<byte> row)
    {
        long sum = 0;
        foreach (var b in row) sum += b < 128 ? b : 256 - b;
        return sum;
    }

    static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        var crc = Crc32.Append(Crc32.Compute(type), data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>Box-filter downscale to at most <paramref name="maxWidth"/> wide (the plugin used 320).</summary>
    public static BgraImage Thumbnail(BgraImage image, int maxWidth = 320)
    {
        if (image.Width <= maxWidth) return image;
        var tw = maxWidth;
        var th = Math.Max(1, (int)Math.Round(image.Height * (double)tw / image.Width));
        var output = new byte[tw * th * 4];
        for (var ty = 0; ty < th; ty++)
        {
            int sy0 = ty * image.Height / th, sy1 = Math.Max(sy0 + 1, (ty + 1) * image.Height / th);
            for (var tx = 0; tx < tw; tx++)
            {
                int sx0 = tx * image.Width / tw, sx1 = Math.Max(sx0 + 1, (tx + 1) * image.Width / tw);
                int b = 0, g = 0, r = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * image.Width * 4;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var p = row + sx * 4;
                        b += image.Pixels[p];
                        g += image.Pixels[p + 1];
                        r += image.Pixels[p + 2];
                        n++;
                    }
                }
                var o = (ty * tw + tx) * 4;
                output[o] = (byte)((b + n / 2) / n);
                output[o + 1] = (byte)((g + n / 2) / n);
                output[o + 2] = (byte)((r + n / 2) / n);
                output[o + 3] = 255;
            }
        }
        return new BgraImage(tw, th, output);
    }
}
