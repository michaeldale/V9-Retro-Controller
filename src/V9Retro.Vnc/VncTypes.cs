namespace V9Retro.Vnc;

/// <summary>A rectangle of the remote framebuffer, in pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct VncRect(int X, int Y, int Width, int Height);

/// <summary>A point on the remote framebuffer.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public readonly record struct VncPoint(int X, int Y);

/// <summary>
/// A cursor image from the RichCursor pseudo-encoding, as straight-alpha BGRA
/// (alpha is 0 or 255). A 0x0 shape means the server hides the cursor.
/// </summary>
public sealed class CursorShape
{
    /// <summary>Creates a cursor shape.</summary>
    public CursorShape(int width, int height, VncPoint hotspot, byte[] bgra)
    {
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("Cursor pixels must be width * height * 4 bytes.", nameof(bgra));
        Width = width;
        Height = height;
        Hotspot = hotspot;
        Bgra = bgra;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel within the image that marks the pointer position.</summary>
    public VncPoint Hotspot { get; }

    /// <summary>Pixels, BGRA, stride <c>Width * 4</c>; transparent pixels have alpha 0.</summary>
    public byte[] Bgra { get; }

    /// <summary>True for the empty (hidden) cursor.</summary>
    public bool IsEmpty => Width == 0 || Height == 0;
}

/// <summary>An RFB PIXEL_FORMAT, as the server announced it in ServerInit.</summary>
public sealed record VncPixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColour,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    /// <summary>32 bpp, depth 24, little-endian, red at bit 16: bytes on the wire are B, G, R, X.</summary>
    public static VncPixelFormat Bgrx32 { get; } = new(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    internal static VncPixelFormat Parse(ReadOnlySpan<byte> b) => new(
        b[0], b[1], b[2] != 0, b[3] != 0,
        (ushort)((b[4] << 8) | b[5]), (ushort)((b[6] << 8) | b[7]), (ushort)((b[8] << 8) | b[9]),
        b[10], b[11], b[12]);

    internal void Write(Span<byte> b)
    {
        b[0] = BitsPerPixel;
        b[1] = Depth;
        b[2] = BigEndian ? (byte)1 : (byte)0;
        b[3] = TrueColour ? (byte)1 : (byte)0;
        b[4] = (byte)(RedMax >> 8); b[5] = (byte)RedMax;
        b[6] = (byte)(GreenMax >> 8); b[7] = (byte)GreenMax;
        b[8] = (byte)(BlueMax >> 8); b[9] = (byte)BlueMax;
        b[10] = RedShift;
        b[11] = GreenShift;
        b[12] = BlueShift;
        b[13] = b[14] = b[15] = 0;
    }
}

/// <summary>RFB encoding numbers used by this client.</summary>
public static class VncEncoding
{
    /// <summary>Raw pixels.</summary>
    public const int Raw = 0;
    /// <summary>Copy a rectangle from elsewhere in the framebuffer.</summary>
    public const int CopyRect = 1;
    /// <summary>16x16 tiles with background, foreground and subrectangles.</summary>
    public const int Hextile = 5;
    /// <summary>Zlib run-length encoding over 64x64 tiles.</summary>
    public const int Zrle = 16;
    /// <summary>Pseudo-encoding: the framebuffer changed size.</summary>
    public const int DesktopSize = -223;
    /// <summary>Pseudo-encoding: the server's cursor position.</summary>
    public const int PointerPos = -232;
    /// <summary>Pseudo-encoding: the cursor shape, in the pixel format, with a bitmask.</summary>
    public const int RichCursor = -239;
}
