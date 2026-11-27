using Bazaar.Sanitization;
using Bazaar.Server.Fun;
using Bazaar.Server.Fun.Sidewalk;
using Bazaar.Server.Sanitization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Fun.Tests;

/// <summary>
/// Verifies FunComponent wires Lucky and Sidewalk from configuration (doc 26 WP7, WP9).
/// </summary>
public class FunComponentWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildProvider(Dictionary<string, string?>? overrides = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides ?? [])
            .Build();

        var services = new ServiceCollection();
        // The host registers the shared sanitizer; the Lucky controller needs it in this bare composition.
        services.AddSingleton<ITextSanitizer, TextSanitizer>();
        var component = new FunComponent();
        component.AddServices(services, config);
        return services.BuildServiceProvider();
    }

    // ── Sidewalk ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sidewalk_settings_are_registered_with_defaults()
    {
        using var sp = BuildProvider();
        var settings = sp.GetRequiredService<SidewalkSettings>();
        Assert.Equal(0, settings.DailyTokenCap);
    }

    [Fact]
    public void Sidewalk_settings_are_bound_from_configuration()
    {
        using var sp = BuildProvider(new() { ["Bazaar:Sidewalk:DailyTokenCap"] = "500" });
        var settings = sp.GetRequiredService<SidewalkSettings>();
        Assert.Equal(500, settings.DailyTokenCap);
    }

    [Fact]
    public void Sidewalk_negative_token_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildProvider(new() { ["Bazaar:Sidewalk:DailyTokenCap"] = "-1" }));
    }
}
