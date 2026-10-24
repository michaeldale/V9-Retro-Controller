using System.Buffers.Binary;

namespace V9Retro.Protocol.Wire;

/// <summary>Builds a little-endian request payload.</summary>
public sealed class PayloadWriter
{
    byte[] _buffer;
    int _length;

    public PayloadWriter(int capacity = 64) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length => _length;

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    Span<byte> Grab(int count)
    {
        if (_length + count > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public PayloadWriter U8(byte value) { Grab(1)[0] = value; return this; }
    public PayloadWriter U16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(Grab(2), value); return this; }
    public PayloadWriter S16(short value) { BinaryPrimitives.WriteInt16LittleEndian(Grab(2), value); return this; }
    public PayloadWriter U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Grab(4), value); return this; }
    public PayloadWriter S32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Grab(4), value); return this; }
    public PayloadWriter Bytes(ReadOnlySpan<byte> data) { data.CopyTo(Grab(data.Length)); return this; }

    /// <summary>u16 byte length, then non-NUL 7-bit ASCII.</summary>
    public PayloadWriter String(string value)
    {
        foreach (var ch in value)
            if (ch == '\0' || ch > 0x7F)
                throw new ArgumentException("protocol strings must be non-NUL 7-bit ASCII", nameof(value));
        if (value.Length > ushort.MaxValue)
            throw new ArgumentException("string exceeds the protocol limit", nameof(value));
        U16((ushort)value.Length);
        var span = Grab(value.Length);
        for (var i = 0; i < value.Length; i++) span[i] = (byte)value[i];
        return this;
    }
}
