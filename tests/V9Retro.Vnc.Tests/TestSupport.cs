using System.Buffers.Binary;

namespace V9Retro.Vnc.Tests;

/// <summary>Builds RFB byte sequences: big-endian integers, BGRX pixels.</summary>
internal sealed class Wire
{
    private readonly List<byte> _bytes = [];

    public Wire U8(int value) { _bytes.Add((byte)value); return this; }

    public Wire U16(int value) { _bytes.Add((byte)(value >> 8)); _bytes.Add((byte)value); return this; }

    public Wire U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public Wire S32(int value) => U32((uint)value);

    public Wire Bytes(params byte[] bytes) { _bytes.AddRange(bytes); return this; }

    /// <summary>A 32 bpp B, G, R, X pixel from 0xRRGGBB.</summary>
    public Wire Pixel(uint rgb) => Bytes((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16), 0);

    /// <summary>A 3-byte ZRLE CPIXEL from 0xRRGGBB.</summary>
    public Wire CPixel(uint rgb) => Bytes((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));

    public Wire Rect(int x, int y, int w, int h, int encoding) => U16(x).U16(y).U16(w).U16(h).S32(encoding);

    public int Length => _bytes.Count;

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>
/// A C# port of the server's src\guest\vncz.c: zlib header once, then per call a
/// fixed-Huffman block with greedy LZ77 and a sync flush. Used so the ZRLE tests feed
/// the decoder exactly the kind of stream V9XVNC.EXE produces.
/// </summary>
internal sealed class FixedDeflate
{
    private static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly byte[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private const int HashSize = 4096;

    private bool _headerWritten;
    private readonly List<byte> _out = [];
    private ulong _bits;
    private int _count;

    private void Put(ulong value, int count)
    {
        _bits |= value << _count;
        _count += count;
        while (_count >= 8)
        {
            _out.Add((byte)_bits);
            _bits >>= 8;
            _count -= 8;
        }
    }

    private void Code(uint code, int count)
    {
        uint reversed = 0;
        for (int i = 0; i < count; i++)
        {
            reversed = (reversed << 1) | (code & 1);
            code >>= 1;
        }
        Put(reversed, count);
    }

    private void Symbol(uint symbol)
    {
        if (symbol < 144) Code(0x30 + symbol, 8);
        else if (symbol < 256) Code(0x190 + symbol - 144, 9);
        else if (symbol < 280) Code(symbol - 256, 7);
        else Code(0xc0 + symbol - 280, 8);
    }

    private void Match(int length, int distance)
    {
        int index = 28;
        while (LengthBase[index] > length) --index;
        Symbol((uint)(257 + index));
        Put((ulong)(length - LengthBase[index]), LengthExtra[index]);
        index = 29;
        while (DistanceBase[index] > distance) --index;
        Code((uint)index, 5);
        Put((ulong)(distance - DistanceBase[index]), DistanceExtra[index]);
    }

    private static int Hash3(byte[] data, int at) =>
        (((data[at] << 8) | data[at + 1]) ^ (data[at + 2] << 4)) & (HashSize - 1);

    public byte[] Sync(byte[] input)
    {
        _out.Clear();
        _bits = 0;
        _count = 0;
        if (!_headerWritten)
        {
            Put(0x78, 8);
            Put(0x01, 8);
            _headerWritten = true;
        }
        Put(0, 1);
        Put(1, 2);
        var head = new int[HashSize];
        int position = 0;
        while (position < input.Length)
        {
            int best = 0, bestDistance = 0;
            if (position + 3 <= input.Length)
            {
                int hash = Hash3(input, position);
                int candidate = head[hash];
                head[hash] = position + 1;
                if (candidate != 0 && position - (candidate - 1) <= 32768)
                {
                    int from = candidate - 1;
                    int limit = Math.Min(input.Length - position, 258);
                    int run = 0;
                    while (run < limit && input[from + run] == input[position + run]) ++run;
                    if (run >= 3)
                    {
                        best = run;
                        bestDistance = position - from;
                    }
                }
            }
            if (best != 0)
            {
                Match(best, bestDistance);
                for (int step = 1; step < best; step++)
                    if (position + step + 3 <= input.Length) head[Hash3(input, position + step)] = position + step + 1;
                position += best;
            }
            else
            {
                Symbol(input[position]);
                ++position;
            }
        }
        Symbol(256);
        Put(0, 3);
        if (_count > 0) Put(0, 8 - _count);
        Put(0x0000, 16);
        Put(0xffff, 16);
        return [.. _out];
    }
}
