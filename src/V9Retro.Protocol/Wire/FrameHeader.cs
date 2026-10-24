using System.Buffers.Binary;

namespace V9Retro.Protocol.Wire;

/// <summary>The 24-byte frame header: "V9XR", u16 version, u16 type, u32 request ID,
/// u32 flags, u32 payload length, u32 reserved. All little-endian.</summary>
public readonly record struct FrameHeader(ushort Type, uint RequestId, uint Flags, int PayloadLength)
{
    public const int Size = 24;
    public const ushort ProtocolVersion = 0x0100;
    public const int MaxPayload = 65536;
    public const int DefaultPort = 9869;

    static ReadOnlySpan<byte> Magic => "V9XR"u8;

    public void Write(Span<byte> target)
    {
        Magic.CopyTo(target);
        BinaryPrimitives.WriteUInt16LittleEndian(target[4..], ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(target[6..], Type);
        BinaryPrimitives.WriteUInt32LittleEndian(target[8..], RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(target[12..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(target[16..], (uint)PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(target[20..], 0);
    }

    public static FrameHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new V9xProtocolException("truncated frame header");
        if (!source[..4].SequenceEqual(Magic))
            throw new V9xProtocolException("invalid V9XR frame magic");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        if (version != ProtocolVersion)
            throw new V9xProtocolException($"unsupported frame version 0x{version:X4}");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[16..]);
        if (length > MaxPayload)
            throw new V9xProtocolException("peer payload exceeds the protocol limit");
        if (BinaryPrimitives.ReadUInt32LittleEndian(source[20..]) != 0)
            throw new V9xProtocolException("reserved header field is nonzero");
        return new FrameHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..]),
            (int)length);
    }
}

/// <summary>A received frame. The payload buffer is owned by the frame.</summary>
public readonly record struct Frame(FrameHeader Header, byte[] Payload)
{
    public ushort Type => Header.Type;
    public uint RequestId => Header.RequestId;
}
