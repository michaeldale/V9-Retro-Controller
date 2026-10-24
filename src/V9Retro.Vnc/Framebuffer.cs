using System.Runtime.InteropServices;

namespace V9Retro.Vnc;

/// <summary>
/// The client's copy of the remote screen: BGRA, 4 bytes per pixel, alpha always 255.
/// Decoders write through the methods here, each of which takes the lock briefly, so a
/// renderer holding <see cref="Lock"/> waits at most one tile. Events are raised on the
/// client's receive thread, never while the lock is held.
/// </summary>
public sealed class Framebuffer
{
    private readonly object _gate = new();
    private byte[] _pixels;
    private int _width;
    private int _height;

    /// <summary>Creates a black framebuffer.</summary>
    public Framebuffer(int width, int height)
    {
        _width = width;
        _height = height;
        _pixels = NewPixels(width, height);
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get { lock (_gate) return _width; } }

    /// <summary>Height in pixels.</summary>
    public int Height { get { lock (_gate) return _height; } }

    /// <summary>Bytes per row (always <c>Width * 4</c>).</summary>
    public int Stride { get { lock (_gate) return _width * 4; } }

    /// <summary>Raised after an update message, with the rectangles it changed.</summary>
    public event Action<IReadOnlyList<VncRect>>? FramebufferUpdated;

    /// <summary>Raised after the framebuffer changed size (DesktopSize); the new contents are black until updated.</summary>
    public event Action<int, int>? Resized;

    /// <summary>Raised when the server sends a new cursor image, with its hotspot.</summary>
    public event Action<CursorShape, VncPoint>? CursorChanged;

    /// <summary>Raised when the server reports where its cursor is (PointerPos).</summary>
    public event Action<VncPoint>? CursorMoved;

    /// <summary>The most recent cursor shape, or null before the server has sent one.</summary>
    public CursorShape? Cursor { get; private set; }

    /// <summary>The most recent server-side cursor position, or null.</summary>
    public VncPoint? CursorPosition { get; private set; }

    /// <summary>
    /// Takes the lock and gives read access to the pixels until disposed. Keep it short:
    /// the receive thread waits for it.
    /// </summary>
    public FramebufferLock Lock()
    {
        Monitor.Enter(_gate);
        return new FramebufferLock(this);
    }

    /// <summary>Copies the current pixels (BGRA, stride <c>width * 4</c>).</summary>
    public byte[] Snapshot(out int width, out int height)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            return (byte[])_pixels.Clone();
        }
    }

    /// <summary>Reads one pixel as 0xAARRGGBB.</summary>
    public uint GetPixel(int x, int y)
    {
        lock (_gate)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
                throw new ArgumentOutOfRangeException(nameof(x));
            return MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan())[y * _width + x];
        }
    }

    internal readonly struct Access
    {
        public Access(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
    }

    internal void Exit() => Monitor.Exit(_gate);

    internal Access Current => new(_pixels, _width, _height);

    internal void Resize(int width, int height)
    {
        lock (_gate)
        {
            _width = width;
            _height = height;
            _pixels = NewPixels(width, height);
        }
        Resized?.Invoke(width, height);
    }

    /// <summary>Fills a rectangle with one 0xAARRGGBB pixel.</summary>
    internal void Fill(int x, int y, int width, int height, uint pixel)
    {
        lock (_gate)
        {
            CheckRect(x, y, width, height);
            var all = MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan());
            for (int row = 0; row < height; row++)
                all.Slice((y + row) * _width + x, width).Fill(pixel);
        }
    }

    /// <summary>Writes <paramref name="width"/> x <paramref name="height"/> pixels, rows packed.</summary>
    internal void Write(int x, int y, int width, int height, ReadOnlySpan<uint> source)
    {
        lock (_gate)
        {
            CheckRect(x, y, width, height);
            var all = MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan());
            for (int row = 0; row < height; row++)
                source.Slice(row * width, width).CopyTo(all.Slice((y + row) * _width + x, width));
        }
    }

    /// <summary>CopyRect: copies from the framebuffer as it was before the copy (overlap-safe).</summary>
    internal void Copy(int sourceX, int sourceY, int x, int y, int width, int height)
    {
        lock (_gate)
        {
            CheckRect(x, y, width, height);
            CheckRect(sourceX, sourceY, width, height);
            var all = MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan());
            // Moving down: copy the bottom row first so no source row is overwritten early.
            // Span.CopyTo is memmove, so overlap within a row is fine.
            if (y > sourceY)
            {
                for (int row = height - 1; row >= 0; row--)
                    all.Slice((sourceY + row) * _width + sourceX, width).CopyTo(all.Slice((y + row) * _width + x, width));
            }
            else
            {
                for (int row = 0; row < height; row++)
                    all.Slice((sourceY + row) * _width + sourceX, width).CopyTo(all.Slice((y + row) * _width + x, width));
            }
        }
    }

    internal void SetCursor(CursorShape shape)
    {
        Cursor = shape;
        CursorChanged?.Invoke(shape, shape.Hotspot);
    }

    internal void SetCursorPosition(VncPoint point)
    {
        CursorPosition = point;
        CursorMoved?.Invoke(point);
    }

    internal void RaiseUpdated(IReadOnlyList<VncRect> dirty) => FramebufferUpdated?.Invoke(dirty);

    private void CheckRect(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || x + width > _width || y + height > _height)
            throw new VncProtocolException(
                $"Rectangle {width}x{height} at {x},{y} is outside the {_width}x{_height} framebuffer.");
    }

    private static byte[] NewPixels(int width, int height)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
        var pixels = new byte[width * height * 4];
        MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(0xFF000000u);
        return pixels;
    }
}

/// <summary>Read access to the framebuffer while its lock is held. Dispose to release.</summary>
public readonly ref struct FramebufferLock
{
    private readonly Framebuffer _owner;
    private readonly byte[] _pixels;

    internal FramebufferLock(Framebuffer owner)
    {
        _owner = owner;
        var access = owner.Current;
        _pixels = access.Pixels;
        Width = access.Width;
        Height = access.Height;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;

    /// <summary>The pixels, BGRA. Valid only until this lock is disposed.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>Releases the lock.</summary>
    public void Dispose() => _owner.Exit();
}
