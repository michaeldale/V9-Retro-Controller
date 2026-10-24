namespace V9Retro.Vnc;

/// <summary>
/// RFB VNC authentication (security type 2): the 16-byte challenge is DES-ECB
/// encrypted with the password, whose bytes go in bit-reversed. .NET's DES refuses
/// weak keys (an empty password is one), so this carries its own small DES, a port
/// of the server's vncdes.c.
/// </summary>
public static class VncAuth
{
    /// <summary>Answers a 16-byte challenge with the given password (at most 8 characters are used).</summary>
    public static byte[] Response(string? password, ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 16)
            throw new ArgumentException("A VNC challenge is 16 bytes.", nameof(challenge));
        var key = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            byte c = password != null && i < password.Length ? (byte)password[i] : (byte)0;
            byte reversed = 0;
            for (int bit = 0; bit < 8; bit++)
                reversed = (byte)((reversed << 1) | ((c >> bit) & 1));
            key[i] = reversed;
        }
        var response = new byte[16];
        Des.Encrypt(key, challenge[..8], response.AsSpan(0, 8));
        Des.Encrypt(key, challenge[8..], response.AsSpan(8, 8));
        return response;
    }
}

/// <summary>Single-block DES encryption (FIPS 46-3), bit per byte, as in vncdes.c.</summary>
internal static class Des
{
    private static readonly byte[] Ip =
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    ];
    private static readonly byte[] Fp =
    [
        40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25,
    ];
    private static readonly byte[] E =
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11,
        12, 13, 12, 13, 14, 15, 16, 17, 16, 17, 18, 19, 20, 21, 20, 21,
        22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    ];
    private static readonly byte[] P =
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    ];
    private static readonly byte[] Pc1 =
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18,
        10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22,
        14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4,
    ];
    private static readonly byte[] Pc2 =
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4,
        26, 8, 16, 7, 27, 20, 13, 2, 41, 52, 31, 37, 47, 55, 30, 40,
        51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    ];
    private static readonly byte[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];
    private static readonly byte[][] S =
    [
        [
            14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
            0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
            4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
            15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,
        ],
        [
            15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
            3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
            0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
            13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,
        ],
        [
            10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
            13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
            13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
            1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,
        ],
        [
            7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
            13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
            10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
            3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,
        ],
        [
            2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
            14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
            4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
            11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,
        ],
        [
            12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
            10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
            9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
            4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,
        ],
        [
            4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
            13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
            1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
            6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,
        ],
        [
            13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
            1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
            7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
            2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
        ],
    ];

    // Bit 1 of the standard is the most significant bit of byte 0.
    private static void Unpack(ReadOnlySpan<byte> bytes, Span<byte> bits)
    {
        for (int i = 0; i < bits.Length; i++)
            bits[i] = (byte)((bytes[i / 8] >> (7 - i % 8)) & 1);
    }

    private static void Permute(ReadOnlySpan<byte> from, Span<byte> to, byte[] table)
    {
        for (int i = 0; i < table.Length; i++)
            to[i] = from[table[i] - 1];
    }

    public static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> keyBits = stackalloc byte[64];
        Span<byte> cd = stackalloc byte[56];
        Span<byte> subkey = stackalloc byte[48];
        Span<byte> block = stackalloc byte[64];
        Span<byte> lr = stackalloc byte[64];
        Span<byte> expanded = stackalloc byte[48];
        Span<byte> sboxOut = stackalloc byte[32];
        Span<byte> mixed = stackalloc byte[32];
        Span<byte> preoutput = stackalloc byte[64];

        Unpack(key, keyBits);
        Permute(keyBits, cd, Pc1);
        Unpack(input, block);
        Permute(block, lr, Ip);
        for (int round = 0; round < 16; round++)
        {
            // Rotate C (cd[0..27]) and D (cd[28..55]) left, then pick the key.
            for (int shift = 0; shift < Shifts[round]; shift++)
            {
                byte c0 = cd[0];
                cd[1..28].CopyTo(cd);
                cd[27] = c0;
                byte d0 = cd[28];
                cd[29..56].CopyTo(cd[28..]);
                cd[55] = d0;
            }
            Permute(cd, subkey, Pc2);
            // f(R, K): expand, mix in the key, S-boxes, permute.
            Permute(lr[32..], expanded, E);
            for (int i = 0; i < 48; i++) expanded[i] ^= subkey[i];
            for (int i = 0; i < 8; i++)
            {
                var six = expanded.Slice(i * 6, 6);
                int row = (six[0] << 1) | six[5];
                int column = (six[1] << 3) | (six[2] << 2) | (six[3] << 1) | six[4];
                int value = S[i][row * 16 + column];
                sboxOut[i * 4] = (byte)((value >> 3) & 1);
                sboxOut[i * 4 + 1] = (byte)((value >> 2) & 1);
                sboxOut[i * 4 + 2] = (byte)((value >> 1) & 1);
                sboxOut[i * 4 + 3] = (byte)(value & 1);
            }
            Permute(sboxOut, mixed, P);
            // L, R = R, L ^ f(R, K)
            for (int i = 0; i < 32; i++)
            {
                byte r = lr[32 + i];
                lr[32 + i] = (byte)(lr[i] ^ mixed[i]);
                lr[i] = r;
            }
        }
        // The last round's halves go out swapped: R16 L16.
        lr[32..].CopyTo(preoutput);
        lr[..32].CopyTo(preoutput[32..]);
        Permute(preoutput, block, Fp);
        for (int i = 0; i < 8; i++)
        {
            int b = 0;
            for (int bit = 0; bit < 8; bit++) b = (b << 1) | block[i * 8 + bit];
            output[i] = (byte)b;
        }
    }
}
