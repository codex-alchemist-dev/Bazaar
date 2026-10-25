using Bazaar.Testing;

namespace Bazaar.Server.Tests.Testing;

public class TestSeedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_or_blank_value_gives_a_random_seed(string? value)
    {
        // Two independent random draws colliding is a 2^-64 event, so inequality is the observable behaviour.
        Assert.NotEqual(TestSeed.Resolve(value), TestSeed.Resolve(value));
    }

    [Fact]
    public void Pinned_value_is_used_exactly()
    {
        Assert.Equal(new TestSeed(12345), TestSeed.Resolve("12345"));
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        Assert.Equal(new TestSeed(77), TestSeed.Resolve("  77 "));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("18446744073709551616")]
    public void Value_that_is_not_an_unsigned_integer_is_rejected(string value)
    {
        var error = Assert.Throws<FormatException>(() => TestSeed.Resolve(value));

        Assert.Contains(TestSeed.EnvironmentVariable, error.Message);
    }

    [Fact]
    public void ToString_is_the_bare_decimal_value_so_it_can_be_pasted_back()
    {
        Assert.Equal("9001", new TestSeed(9001).ToString());
    }
}
