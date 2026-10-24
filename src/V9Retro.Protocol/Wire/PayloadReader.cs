using System.Buffers.Binary;
using System.Text;

namespace V9Retro.Protocol.Wire;

/// <summary>Reads a little-endian response payload. Every read is bounds-checked and
/// throws <see cref="V9xProtocolException"/> on truncation.</summary>
public ref struct PayloadReader
{
    readonly ReadOnlySpan<byte> _data;
    readonly string _what;
    int _offset;

    public PayloadReader(ReadOnlySpan<byte> data, string what)
    {
        _data = data;
        _what = what;
        _offset = 0;
    }

    public readonly int Offset => _offset;
    public readonly int Remaining => _data.Length - _offset;
    public readonly bool AtEnd => _offset >= _data.Length;

    ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _offset + count > _data.Length)
            throw new V9xProtocolException($"truncated {_what}");
        var span = _data.Slice(_offset, count);
        _offset += count;
        return span;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short S16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int S32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ReadOnlySpan<byte> Bytes(int count) => Take(count);
    public void Skip(int count) => Take(count);
    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    /// <summary>A protocol string the agent built from fixed ASCII.</summary>
    public string String() => Encoding.ASCII.GetString(Take(U16()));

    /// <summary>A protocol string the guest filled from Windows itself (paths, titles,
    /// registry names): ANSI, decoded as Windows-1252.</summary>
    public string AnsiString() => GuestText.Decode(Take(U16()));

    public readonly void EnsureEnd()
    {
        if (_offset != _data.Length)
            throw new V9xProtocolException($"{_what} contains trailing data");
    }
}

/// <summary>Guest text is ANSI. Windows-1252 covers the Western installs the agent targets.</summary>
public static class GuestText
{
    static readonly Encoding Ansi = CreateAnsi();

    static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    public static string Decode(ReadOnlySpan<byte> bytes) => Ansi.GetString(bytes);
}
