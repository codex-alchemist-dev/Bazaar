using System.Text;
using Bazaar.Server.Identity.Totp;

namespace Bazaar.Server.Tests.Identity.Totp;

public sealed class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("MY======", "f")]
    [InlineData("MZXQ====", "fo")]
    [InlineData("MZXW6===", "foo")]
    [InlineData("MZXW6YQ=", "foob")]
    [InlineData("MZXW6YTB", "fooba")]
    [InlineData("MZXW6YTBOI======", "foobar")]
    public void Decodes_rfc4648_vectors(string encoded, string expected)
    {
        Assert.Equal(expected, Encoding.ASCII.GetString(Base32.Decode(encoded)));
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.Equal(Base32.Decode("MZXW6YTBOI======"), Base32.Decode("mzxw6ytboi"));
    }

    [Fact]
    public void Ignores_spaces_and_padding()
    {
        Assert.Equal(Base32.Decode("MZXW6YTBOI======"), Base32.Decode("MZXW 6YTB OI"));
        Assert.Equal(Base32.Decode("MZXW6==="), Base32.Decode("MZXW6"));
    }

    [Fact]
    public void Decodes_an_otpauth_style_seed()
    {
        // Public example seed from common authenticator documentation; it decodes to "Hello!" followed by 0xDEADBEEF.
        byte[] seed = Base32.Decode("JBSWY3DPEHPK3PXP");

        Assert.Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0xDE, 0xAD, 0xBE, 0xEF }, seed);
    }

    [Theory]
    [InlineData("MZXW6Y1B")] // '1' is not in the alphabet
    [InlineData("MZXW6Y0B")] // '0' is not in the alphabet
    [InlineData("MZXW-6YT")]
    [InlineData("MZXW6YT!")]
    [InlineData("MZXW6YTé")]
    public void Rejects_characters_outside_the_alphabet(string input)
    {
        Assert.Throws<ArgumentException>(() => Base32.Decode(input));
    }

    [Fact]
    public void Error_message_does_not_echo_the_input()
    {
        const string secretLikeInput = "JBSWY3DP1SECRET";

        var ex = Assert.Throws<ArgumentException>(() => Base32.Decode(secretLikeInput));

        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.DoesNotContain(secretLikeInput, ex.Message);
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => Base32.Decode(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(64)]
    public void Round_trips_random_bytes(int length)
    {
        byte[] original = new byte[length];
        Random.Shared.NextBytes(original);

        byte[] decoded = Base32.Decode(Encode(original));

        Assert.Equal(original, decoded);
    }

    /// <summary>Test-only RFC 4648 encoder, used to build round-trip inputs.</summary>
    private static string Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0;
        int bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return sb.ToString();
    }
}
