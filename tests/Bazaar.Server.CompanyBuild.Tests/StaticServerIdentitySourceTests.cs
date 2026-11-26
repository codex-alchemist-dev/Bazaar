using Bazaar.Server.CompanyBuild;
using Xunit;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class StaticServerIdentitySourceTests
{
    private static readonly string _pin = new('a', 64);

    [Fact]
    public void Carries_address_and_pin()
    {
        var source = new StaticServerIdentitySource(new Uri("https://bazaar.example/"), _pin);
        Assert.Equal("https://bazaar.example/", source.Address.AbsoluteUri);
        Assert.Equal(_pin, source.KeyHashPin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Refuses_a_malformed_pin(string pin) =>
        Assert.Throws<ArgumentException>(() => new StaticServerIdentitySource(new Uri("https://bazaar.example/"), pin));

    [Fact]
    public void Refuses_a_relative_address() =>
        Assert.Throws<ArgumentException>(() => new StaticServerIdentitySource(new Uri("/x", UriKind.Relative), _pin));
}
