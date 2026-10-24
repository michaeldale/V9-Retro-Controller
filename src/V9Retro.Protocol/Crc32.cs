namespace V9Retro.Protocol;

/// <summary>Standard reflected CRC-32 (polynomial 0xEDB88320), as the agent and zlib compute it.</summary>
public static class Crc32
{
    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Continues a running CRC; start with 0.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);
}
