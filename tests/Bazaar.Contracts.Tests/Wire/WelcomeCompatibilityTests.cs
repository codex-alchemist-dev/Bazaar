using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Wire;

public class WelcomeCompatibilityTests
{
    private static Welcome Make(bool? upgrade = null, string? build = null, string? schema = null, bool? previous = null, int version = 1, int minor = 2) =>
        new(version, minor, "json", Guid.Empty, DateTimeOffset.UnixEpoch, 20, 60, new WireLimits(1, 1, 1), [], 0, upgrade, build, schema, previous);

    [Fact]
    public void Reports_protocol_as_major_dot_minor_and_the_new_values()
    {
        var c = WelcomeCompatibility.From(Make(schema: "12", previous: true));

        Assert.Equal("1.2", c.ServerProtocol);
        Assert.Equal("1.2", c.ChosenProtocol);
        Assert.Equal("12", c.SchemaVersion);
        Assert.True(c.PreviousMajorSupported);
        Assert.False(c.UpgradeRequired);
    }

    [Fact]
    public void Unknown_values_are_null()
    {
        var c = WelcomeCompatibility.From(Make(schema: "  "));

        Assert.Null(c.SchemaVersion);
        Assert.Null(c.PreviousMajorSupported);
        Assert.Null(c.MinimumBuild);
    }

    [Fact]
    public void Upgrade_required_chooses_no_protocol_and_carries_the_minimum_build()
    {
        var c = WelcomeCompatibility.From(Make(upgrade: true, build: "0.2.0", previous: false));

        Assert.True(c.UpgradeRequired);
        Assert.Null(c.ChosenProtocol);
        Assert.Equal("1.2", c.ServerProtocol);
        Assert.Equal("0.2.0", c.MinimumBuild);
        Assert.False(c.PreviousMajorSupported);
    }

    [Fact]
    public void Negative_parts_format_as_null() => Assert.Null(WelcomeCompatibility.Format(-1, 0));
}
