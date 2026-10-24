namespace V9Retro.Vnc.Decoding;

/// <summary>Raw: width x height pixels, row by row.</summary>
internal static class RawDecoder
{
    // Rows are read in bands so a large rect never holds the framebuffer lock for long.
    private const int BandPixels = 64 * 1024;

    public static void Decode(RfbReader reader, Framebuffer target, VncRect rect)
    {
        if (rect.Width == 0 || rect.Height == 0) return;
        int bandRows = Math.Max(1, BandPixels / rect.Width);
        var buffer = new uint[Math.Min(bandRows, rect.Height) * rect.Width];
        for (int row = 0; row < rect.Height; row += bandRows)
        {
            int rows = Math.Min(bandRows, rect.Height - row);
            var band = buffer.AsSpan(0, rows * rect.Width);
            reader.ReadPixels(band);
            target.Write(rect.X, rect.Y + row, rect.Width, rows, band);
        }
    }
}

/// <summary>CopyRect: the source position; pixels come from the framebuffer itself.</summary>
internal static class CopyRectDecoder
{
    public static void Decode(RfbReader reader, Framebuffer target, VncRect rect)
    {
        int sourceX = reader.ReadU16();
        int sourceY = reader.ReadU16();
        target.Copy(sourceX, sourceY, rect.X, rect.Y, rect.Width, rect.Height);
    }
}

/// <summary>Hextile (RFC 6143 7.7.4): 16x16 tiles, row-major.</summary>
internal static class HextileDecoder
{
    public const byte Raw = 1;
    public const byte BackgroundSpecified = 2;
    public const byte ForegroundSpecified = 4;
    public const byte AnySubrects = 8;
    public const byte SubrectsColoured = 16;

    public static void Decode(RfbReader reader, Framebuffer target, VncRect rect)
    {
        Span<uint> tile = stackalloc uint[16 * 16];
        uint background = 0xFF000000u;
        uint foreground = 0xFF000000u;
        for (int tileY = rect.Y; tileY < rect.Y + rect.Height; tileY += 16)
        {
            int height = Math.Min(16, rect.Y + rect.Height - tileY);
            for (int tileX = rect.X; tileX < rect.X + rect.Width; tileX += 16)
            {
                int width = Math.Min(16, rect.X + rect.Width - tileX);
                var pixels = tile[..(width * height)];
                byte flags = reader.ReadU8();
                if ((flags & Raw) != 0)
                {
                    reader.ReadPixels(pixels);
                    target.Write(tileX, tileY, width, height, pixels);
                    continue;
                }
                if ((flags & BackgroundSpecified) != 0) background = reader.ReadPixel();
                if ((flags & ForegroundSpecified) != 0) foreground = reader.ReadPixel();
                pixels.Fill(background);
                if ((flags & AnySubrects) != 0)
                {
                    int count = reader.ReadU8();
                    bool coloured = (flags & SubrectsColoured) != 0;
                    for (int i = 0; i < count; i++)
                    {
                        uint colour = coloured ? reader.ReadPixel() : foreground;
                        byte xy = reader.ReadU8();
                        byte wh = reader.ReadU8();
                        int sx = xy >> 4, sy = xy & 15;
                        int sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                        if (sx + sw > width || sy + sh > height)
                            throw new VncProtocolException("Hextile subrectangle lies outside its tile.");
                        for (int row = sy; row < sy + sh; row++)
                            pixels.Slice(row * width + sx, sw).Fill(colour);
                    }
                }
                target.Write(tileX, tileY, width, height, pixels);
            }
        }
    }
}
