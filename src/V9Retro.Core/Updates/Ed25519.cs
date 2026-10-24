using System.Numerics;
using System.Security.Cryptography;

namespace V9Retro.Core.Updates;

/// <summary>Ed25519 signature verification (RFC 8032), verify only. .NET has no Ed25519, and this
/// app only ever checks the release signature, so a small BigInteger implementation in extended
/// twisted-Edwards coordinates is enough; speed is irrelevant for one check per update.</summary>
public static class Ed25519
{
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    static readonly BigInteger D = Mod(-121665 * Inv(121666));
    static readonly BigInteger D2 = Mod(2 * D);
    static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly Point B = Decode(EncodeY(Mod(4 * Inv(5))))!.Value;
    static readonly Point Identity = new(0, 1, 1, 0);

    readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    static BigInteger Mod(BigInteger a)
    {
        var r = a % P;
        return r.Sign < 0 ? r + P : r;
    }

    static BigInteger Inv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    static BigInteger LittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    static byte[] EncodeY(BigInteger y)
    {
        var bytes = new byte[32];
        y.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false);
        return bytes;
    }

    static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * D2 * q.T);
        var d = Mod(2 * p.Z * q.Z);
        BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    static Point Multiply(Point p, BigInteger scalar)
    {
        var result = Identity;
        var addend = p;
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    /// <summary>Decodes a 32-byte point, or null when it is not on the curve.</summary>
    static Point? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32) return null;
        var bytes = encoded.ToArray();
        var sign = (bytes[31] >> 7) & 1;
        bytes[31] &= 0x7F;
        var y = LittleEndian(bytes);
        if (y >= P) return null;
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        // x = u v^3 (u v^7)^((p-5)/8)
        var v3 = Mod(v * v * v);
        var x = Mod(u * v3 * BigInteger.ModPow(Mod(u * v3 * v3 * v), (P - 5) / 8, P));
        var vx2 = Mod(v * x * x);
        if (vx2 != u)
        {
            if (vx2 != Mod(-u)) return null;
            x = Mod(x * SqrtM1);
        }
        if (x.IsZero && sign == 1) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return new Point(x, y, 1, Mod(x * y));
    }

    static bool SamePoint(Point a, Point b) =>
        Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;

    /// <summary>True when <paramref name="signature"/> (64 bytes) is a valid signature of
    /// <paramref name="message"/> under <paramref name="publicKey"/> (32 bytes).</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        if (Decode(publicKey) is not { } a) return false;
        if (Decode(signature[..32]) is not { } r) return false;
        var s = LittleEndian(signature[32..]);
        if (s >= L) return false;

        var hashInput = new byte[64 + message.Length];
        signature[..32].CopyTo(hashInput);
        publicKey.CopyTo(hashInput.AsSpan(32));
        message.CopyTo(hashInput.AsSpan(64));
        var h = LittleEndian(SHA512.HashData(hashInput)) % L;

        // [S]B == R + [h]A
        return SamePoint(Multiply(B, s), Add(r, Multiply(a, h)));
    }
}
