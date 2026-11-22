using Bazaar.Server.Identity.BrowserProfiles;

namespace Bazaar.Server.Tests.Identity.BrowserProfiles;

public class CanaryTests
{
    [Fact]
    public void Seeded_canary_is_detected()
    {
        var canary = CanarySeeder.Seed(BrowserProfileTestKit.ServiceA);

        Assert.True(CanarySeeder.IsCanary(canary));
        Assert.Equal(BrowserProfileTestKit.ServiceA, canary.Domain);
    }

    [Fact]
    public void Canary_names_and_values_are_random()
    {
        var first = CanarySeeder.Seed(BrowserProfileTestKit.ServiceA);
        var second = CanarySeeder.Seed(BrowserProfileTestKit.ServiceA);

        Assert.NotEqual(first.Name, second.Name);
        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void Canary_value_is_plainly_fake()
    {
        var canary = CanarySeeder.Seed(BrowserProfileTestKit.ServiceA);

        Assert.StartsWith("canary-decoy-not-a-credential-", canary.Value);
    }

    [Fact]
    public void Ordinary_cookie_is_not_a_canary()
    {
        var cookie = BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA);

        Assert.False(CanarySeeder.IsCanary(cookie));
    }

    [Fact]
    public void Cookie_ToString_never_shows_the_value()
    {
        var cookie = BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA, "token", "super-secret-value");

        Assert.DoesNotContain("super-secret-value", cookie.ToString());
    }
}
