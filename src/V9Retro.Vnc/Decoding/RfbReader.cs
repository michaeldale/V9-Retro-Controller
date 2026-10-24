using System.Buffers.Binary;

namespace V9Retro.Vnc.Decoding;

/// <summary>Blocking big-endian reads over a stream; end of stream is an error.</summary>
internal sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[4];

    public RfbReader(Stream stream) => _stream = stream;

    public Stream BaseStream => _stream;

    public void ReadExactly(Span<byte> target) => _stream.ReadExactly(target);

    public byte ReadU8()
    {
        int value = _stream.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        return (byte)value;
    }

    public ushort ReadU16()
    {
        _stream.ReadExactly(_scratch, 0, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(_scratch);
    }

    public uint ReadU32()
    {
        _stream.ReadExactly(_scratch, 0, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(_scratch);
    }

    public int ReadS32() => (int)ReadU32();

    public void Skip(long count)
    {
        Span<byte> sink = stackalloc byte[256];
        while (count > 0)
        {
            int part = (int)Math.Min(count, sink.Length);
            _stream.ReadExactly(sink[..part]);
            count -= part;
        }
    }

    /// <summary>One PIXEL in the 32 bpp B, G, R, X format, as opaque 0xAARRGGBB.</summary>
    public uint ReadPixel()
    {
        _stream.ReadExactly(_scratch, 0, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(_scratch) | 0xFF000000u;
    }

    /// <summary>Reads <paramref name="target"/>.Length pixels.</summary>
    public void ReadPixels(Span<uint> target)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(target);
        _stream.ReadExactly(bytes);
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < target.Length; i++) target[i] = BinaryPrimitives.ReverseEndianness(target[i]);
        for (int i = 0; i < target.Length; i++) target[i] |= 0xFF000000u;
    }
}
