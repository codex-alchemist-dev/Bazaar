using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.Image.Tests;

public class OptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Defaults_apply_when_the_section_is_absent()
    {
        PoolImageOptions o = PoolImageOptions.Bind(Config());

        Assert.Equal(["chromium", "bubblewrap", "util-linux", "procps", "nftables", "iproute2", "cage", "apparmor"], o.Packages);
        Assert.Equal(TimeSpan.FromMinutes(5), o.RecheckInterval);
        Assert.Equal("Bazaar:BrowserPool:Image", PoolImageOptions.SectionName);
    }

    [Fact]
    public void Overrides_replace_the_default_list()
    {
        PoolImageOptions o = PoolImageOptions.Bind(Config(
            ("Bazaar:BrowserPool:Image:Packages:0", "chromium-browser"),
            ("Bazaar:BrowserPool:Image:Packages:1", "bubblewrap"),
            ("Bazaar:BrowserPool:Image:RecheckInterval", "00:01:00")));

        Assert.Equal(["chromium-browser", "bubblewrap"], o.Packages);
        Assert.Equal(TimeSpan.FromMinutes(1), o.RecheckInterval);
    }

    [Theory]
    [InlineData("Packages:0", "bad name; id")]
    [InlineData("Packages:0", "UPPER")]
    [InlineData("Packages:0", "-rf")]
    [InlineData("RecheckInterval", "00:00:01")]
    [InlineData("RecheckInterval", "02:00:00")]
    [InlineData("RecheckInterval", "-00:01:00")]
    [InlineData("RecheckInterval", "soon")]
    public void Invalid_values_throw_without_echoing_them(string key, string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PoolImageOptions.Bind(Config(($"Bazaar:BrowserPool:Image:{key}", value))));

        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void The_recheck_range_edges_are_accepted()
    {
        Assert.Empty(new PoolImageOptions { RecheckInterval = TimeSpan.FromSeconds(10) }.Validate());
        Assert.Empty(new PoolImageOptions { RecheckInterval = TimeSpan.FromHours(1) }.Validate());
        Assert.NotEmpty(new PoolImageOptions { RecheckInterval = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1) }.Validate());
    }

    [Fact]
    public void Too_many_or_no_packages_are_invalid()
    {
        Assert.NotEmpty(new PoolImageOptions { Packages = [] }.Validate());
        Assert.NotEmpty(new PoolImageOptions { Packages = Enumerable.Range(0, 101).Select(i => "pkg" + i).ToList() }.Validate());
    }
}
