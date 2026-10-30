using System.Text;
using Bazaar.Server.Identity.Totp;

namespace Bazaar.Server.Tests.Identity.Totp;

/// <summary>RFC 6238 Appendix B vectors. The seeds are the public RFC test values, not secrets.</summary>
public sealed class TotpGeneratorTests
{
    private static readonly byte[] Sha1Seed = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Sha256Seed = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
    private static readonly byte[] Sha512Seed = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 6)) + "1234");

    private static readonly long[] VectorTimes = [59, 1111111109, 1111111111, 1234567890, 2000000000, 20000000000];

    public static TheoryData<long, string> Sha1Vectors => new()
    {
        { 59, "94287082" },
        { 1111111109, "07081804" },
        { 1111111111, "14050471" },
        { 1234567890, "89005924" },
        { 2000000000, "69279037" },
        { 20000000000, "65353130" },
    };

    public static TheoryData<long, string> Sha256Vectors => new()
    {
        { 59, "46119246" },
        { 1111111109, "68084774" },
        { 1111111111, "67062674" },
        { 1234567890, "91819424" },
        { 2000000000, "90698825" },
        { 20000000000, "77737706" },
    };

    public static TheoryData<long, string> Sha512Vectors => new()
    {
        { 59, "90693936" },
        { 1111111109, "25091201" },
        { 1111111111, "99943326" },
        { 1234567890, "93441116" },
        { 2000000000, "38618901" },
        { 20000000000, "47863826" },
    };

    [Theory]
    [MemberData(nameof(Sha1Vectors))]
    public void Sha1_matches_rfc6238_vectors(long unixSeconds, string expected)
    {
        string code = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8);

        Assert.Equal(expected, code);
    }

    [Theory]
    [MemberData(nameof(Sha256Vectors))]
    public void Sha256_matches_rfc6238_vectors(long unixSeconds, string expected)
    {
        string code = TotpGenerator.Generate(Sha256Seed, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8, alg: TotpAlgorithm.Sha256);

        Assert.Equal(expected, code);
    }

    [Theory]
    [MemberData(nameof(Sha512Vectors))]
    public void Sha512_matches_rfc6238_vectors(long unixSeconds, string expected)
    {
        string code = TotpGenerator.Generate(Sha512Seed, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8, alg: TotpAlgorithm.Sha512);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void Defaults_are_six_digits_thirty_seconds_sha1()
    {
        DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(59);

        string defaults = TotpGenerator.Generate(Sha1Seed, at);
        string explicitly = TotpGenerator.Generate(Sha1Seed, at, 6, 30, TotpAlgorithm.Sha1);

        Assert.Equal(6, defaults.Length);
        Assert.Equal(explicitly, defaults);
        Assert.Equal("287082", defaults);
    }

    [Fact]
    public void Codes_stay_constant_within_one_period()
    {
        string start = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(60));
        string end = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(89));
        string next = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(90));

        Assert.Equal(start, end);
        Assert.NotEqual(start, next);
    }

    [Fact]
    public void Custom_period_changes_the_time_step()
    {
        // With a 60 second step, 59 and 60 fall into different counters (0 and 1).
        string zero = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(59), 8, 60);
        string one = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(60), 8, 60);

        Assert.NotEqual(zero, one);
        Assert.Equal("94287082", TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(59), 8, 30));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    public void Rejects_digits_out_of_range(int digits)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TotpGenerator.Generate(Sha1Seed, DateTimeOffset.UnixEpoch, digits));

        Assert.Equal("digits", ex.ParamName);
    }

    [Fact]
    public void Rejects_non_positive_period()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TotpGenerator.Generate(Sha1Seed, DateTimeOffset.UnixEpoch, periodSeconds: 0));

        Assert.Equal("periodSeconds", ex.ParamName);
    }

    [Fact]
    public void Rejects_instants_before_the_unix_epoch()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(-1)));

        Assert.Equal("at", ex.ParamName);
    }

    [Fact]
    public void Rejects_unknown_algorithm()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TotpGenerator.Generate(Sha1Seed, DateTimeOffset.UnixEpoch, alg: (TotpAlgorithm)99));
    }

    [Fact]
    public void Code_is_zero_padded_to_requested_width()
    {
        // Every code must have exactly the requested width, even when the leading digits are zero.
        for (long t = 0; t < 600; t += 30)
        {
            string code = TotpGenerator.Generate(Sha1Seed, DateTimeOffset.FromUnixTimeSeconds(t), 8);
            Assert.Equal(8, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }
}
