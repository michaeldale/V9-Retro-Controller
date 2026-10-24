using System.Security.Cryptography;

namespace V9Retro.Vnc.Tests;

public class VncAuthTests
{
    // The same FIPS 81 / Stallings answers tests\host\test_vnc.c checks vncdes.c against.
    [Theory]
    [InlineData("133457799bbcdff1", "0123456789abcdef", "85e813540f0ab405")]
    [InlineData("0000000000000000", "0000000000000000", "8ca64de9c1b123a7")]
    public void DesKnownAnswers(string key, string plain, string cipher)
    {
        var output = new byte[8];
        Des.Encrypt(Convert.FromHexString(key), Convert.FromHexString(plain), output);
        Assert.Equal(cipher, Convert.ToHexStringLower(output));
    }

    [Fact]
    public void ResponseMatchesPlatformDesWithBitReversedKey()
    {
        var challenge = Challenge();
        // "secret" with each byte bit-reversed, zero padded: not a weak key, so .NET's DES takes it.
        byte[] key = [.. "secret\0\0".Select(c => Reverse((byte)c))];
        using var des = DES.Create();
        des.Key = key;
        var expected = des.EncryptEcb(challenge, PaddingMode.None);

        Assert.Equal(expected, VncAuth.Response("secret", challenge));
    }

    [Fact]
    public void ResponseIsDeterministicAndDependsOnThePassword()
    {
        var challenge = Challenge();
        Assert.Equal(VncAuth.Response("secret", challenge), VncAuth.Response("secret", challenge));
        Assert.NotEqual(VncAuth.Response("secret", challenge), VncAuth.Response("Secret", challenge));
        // Only the first eight characters count.
        Assert.Equal(VncAuth.Response("12345678", challenge), VncAuth.Response("123456789", challenge));
    }

    [Fact]
    public void EmptyPasswordUsesTheAllZeroKey()
    {
        var challenge = new byte[16];
        Assert.Equal("8ca64de9c1b123a78ca64de9c1b123a7", Convert.ToHexStringLower(VncAuth.Response("", challenge)));
    }

    // The challenge test_vnc.c uses: index * 17 + 3.
    private static byte[] Challenge() => [.. Enumerable.Range(0, 16).Select(i => (byte)(i * 17 + 3))];

    private static byte Reverse(byte b)
    {
        byte r = 0;
        for (int bit = 0; bit < 8; bit++) r = (byte)((r << 1) | ((b >> bit) & 1));
        return r;
    }
}
