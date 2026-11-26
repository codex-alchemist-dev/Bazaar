using Bazaar.Server.Fun;
using Bazaar.Server.Fun.Lucky;
using Bazaar.Server.Fun.Sidewalk;
using Bazaar.Sanitization;
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

    // ── Lucky ────────────────────────────────────────────────────────────────

    [Fact]
    public void Lucky_controller_is_registered_with_default_settings()
    {
        using var sp = BuildProvider();
        var controller = sp.GetRequiredService<LuckyController>();
        Assert.NotNull(controller);
    }

    [Fact]
    public void Lucky_settings_are_bound_from_configuration()
    {
        using var sp = BuildProvider(new()
        {
            ["Bazaar:Lucky:MaxIdeas"] = "3",
            ["Bazaar:Lucky:RunMinutes"] = "30",
        });

        var settings = sp.GetRequiredService<LuckySettings>();
        Assert.Equal(3, settings.MaxIdeas);
        Assert.Equal(30, settings.RunMinutes);
    }

    [Fact]
    public void Lucky_settings_validation_throws_for_zero_run_minutes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuildProvider(new() { ["Bazaar:Lucky:RunMinutes"] = "0" }));
    }

    [Fact]
    public void Lucky_controller_wake_list_covers_all_participants()
    {
        using var sp = BuildProvider();
        var controller = sp.GetRequiredService<LuckyController>();

        var candidates = new[]
        {
            new LuckyCandidate("agent-x", OnShift: true, OptedIn: false),
            new LuckyCandidate("agent-y", OnShift: true, OptedIn: true),
        };

        var result = controller.Start("run-1", "stuck on caching", true, DateTimeOffset.UtcNow, 0, candidates, () => 42);

        Assert.True(result.Accepted);
        var wakeList = LuckyController.WakeList(result.Run!);
        Assert.Equal(["agent-y"], wakeList);
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
