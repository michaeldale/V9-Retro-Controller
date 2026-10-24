using System.IO.Compression;

namespace V9Retro.Vnc.Decoding;

/// <summary>
/// ZRLE (RFC 6143 7.7.6). One zlib stream spans the whole connection: each rect's
/// compressed bytes are appended to a feed that a long-lived <see cref="ZLibStream"/>
/// reads from. The server ends every rect with a sync flush, so a rect's tiles can be
/// inflated completely from its own bytes.
/// </summary>
internal sealed class ZrleDecoder : IDisposable
{
    private const int TileSize = 64;
    private readonly FeedStream _feed = new();
    private readonly ZLibStream _inflate;
    private readonly RfbReader _reader;
    private readonly int _cpixelBytes;
    private readonly uint[] _tile = new uint[TileSize * TileSize];
    private readonly uint[] _palette = new uint[128];

    /// <param name="cpixelBytes">3 for a depth-24 32 bpp format, else 4.</param>
    public ZrleDecoder(int cpixelBytes = 3)
    {
        if (cpixelBytes is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(cpixelBytes));
        _cpixelBytes = cpixelBytes;
        _inflate = new ZLibStream(_feed, CompressionMode.Decompress);
        _reader = new RfbReader(_inflate);
    }

    public void Decode(RfbReader network, Framebuffer target, VncRect rect)
    {
        uint length = network.ReadU32();
        if (length > 64u * 1024 * 1024) throw new VncProtocolException($"ZRLE rect of {length} bytes is too large.");
        var compressed = new byte[length];
        network.ReadExactly(compressed);
        _feed.Append(compressed);
        try
        {
            for (int tileY = rect.Y; tileY < rect.Y + rect.Height; tileY += TileSize)
            {
                int height = Math.Min(TileSize, rect.Y + rect.Height - tileY);
                for (int tileX = rect.X; tileX < rect.X + rect.Width; tileX += TileSize)
                {
                    int width = Math.Min(TileSize, rect.X + rect.Width - tileX);
                    DecodeTile(target, tileX, tileY, width, height);
                }
            }
        }
        catch (EndOfStreamException e)
        {
            throw new VncProtocolException("ZRLE data ended inside a tile.", e);
        }
        catch (InvalidDataException e)
        {
            throw new VncProtocolException("ZRLE zlib data is corrupt.", e);
        }
    }

    private void DecodeTile(Framebuffer target, int x, int y, int width, int height)
    {
        int count = width * height;
        var pixels = _tile.AsSpan(0, count);
        byte sub = _reader.ReadU8();
        if (sub == 0)
        {
            for (int i = 0; i < count; i++) pixels[i] = ReadCPixel();
        }
        else if (sub == 1)
        {
            target.Fill(x, y, width, height, ReadCPixel());
            return;
        }
        else if (sub <= 16)
        {
            int size = sub;
            for (int i = 0; i < size; i++) _palette[i] = ReadCPixel();
            int bits = size <= 2 ? 1 : size <= 4 ? 2 : 4;
            int mask = (1 << bits) - 1;
            for (int row = 0; row < height; row++)
            {
                int filled = 0;
                byte current = 0;
                for (int column = 0; column < width; column++)
                {
                    if (filled == 0)
                    {
                        current = _reader.ReadU8();
                        filled = 8;
                    }
                    filled -= bits;
                    int index = (current >> filled) & mask;
                    if (index >= size) throw new VncProtocolException("ZRLE packed palette index out of range.");
                    pixels[row * width + column] = _palette[index];
                }
            }
        }
        else if (sub == 128)
        {
            int at = 0;
            while (at < count)
            {
                uint pixel = ReadCPixel();
                int run = ReadRunLength();
                if (run > count - at) throw new VncProtocolException("ZRLE run passes the end of its tile.");
                pixels.Slice(at, run).Fill(pixel);
                at += run;
            }
        }
        else if (sub >= 130)
        {
            int size = sub - 128;
            for (int i = 0; i < size; i++) _palette[i] = ReadCPixel();
            int at = 0;
            while (at < count)
            {
                byte entry = _reader.ReadU8();
                int index = entry & 127;
                if (index >= size) throw new VncProtocolException("ZRLE palette RLE index out of range.");
                int run = (entry & 128) != 0 ? ReadRunLength() : 1;
                if (run > count - at) throw new VncProtocolException("ZRLE run passes the end of its tile.");
                pixels.Slice(at, run).Fill(_palette[index]);
                at += run;
            }
        }
        else
        {
            throw new VncProtocolException($"Unknown ZRLE tile sub-encoding {sub}.");
        }
        target.Write(x, y, width, height, pixels);
    }

    private int ReadRunLength()
    {
        int length = 1;
        byte b;
        do
        {
            b = _reader.ReadU8();
            length += b;
        } while (b == 255);
        return length;
    }

    private uint ReadCPixel()
    {
        if (_cpixelBytes == 4) return _reader.ReadPixel();
        uint b0 = _reader.ReadU8();
        uint b1 = _reader.ReadU8();
        uint b2 = _reader.ReadU8();
        return b0 | (b1 << 8) | (b2 << 16) | 0xFF000000u;
    }

    public void Dispose() => _inflate.Dispose();

    /// <summary>
    /// The inflater's input: bytes appended per rect. Running dry means the server's
    /// rect was short, which is reported as end of stream rather than a quiet EOF.
    /// </summary>
    private sealed class FeedStream : Stream
    {
        private byte[] _buffer = [];
        private int _offset;

        public void Append(byte[] data)
        {
            if (_offset < _buffer.Length)
            {
                var merged = new byte[_buffer.Length - _offset + data.Length];
                _buffer.AsSpan(_offset).CopyTo(merged);
                data.CopyTo(merged, _buffer.Length - _offset);
                _buffer = merged;
            }
            else
            {
                _buffer = data;
            }
            _offset = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int available = _buffer.Length - _offset;
            if (available == 0) throw new EndOfStreamException("ZRLE rect needs more data than it carried.");
            int n = Math.Min(available, buffer.Length);
            _buffer.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
