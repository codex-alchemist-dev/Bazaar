using Bazaar.Server.Fun;
using Bazaar.Server.Fun.Lucky;
using Bazaar.Server.Fun.Sidewalk;
using Bazaar.Server.Fun.Vent;
using Bazaar.Server.Fun.Ward;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;
using Bazaar.Server.Sanitization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Fun.Sidewalk;

/// <summary>
/// Verifies FunComponent wires Lucky, Ward, Vent and Sidewalk from configuration,
/// and validates the GcTypeKeys constants for the fun system GC types (doc 26 WP7, WP9).
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
        Assert.Equal(["agent-x", "agent-y"], wakeList);
    }

    // ── Ward ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Ward_settings_are_registered_with_defaults()
    {
        using var sp = BuildProvider();
        var settings = sp.GetRequiredService<WardSettings>();
        Assert.Equal(60, settings.MinStayMinutes);
        Assert.Equal(WardAdmissionMode.Automatic, settings.AdmissionMode);
    }

    [Fact]
    public void Ward_settings_are_bound_from_configuration()
    {
        using var sp = BuildProvider(new()
        {
            ["Bazaar:Ward:MinStayMinutes"] = "120",
            ["Bazaar:Ward:AdmissionMode"] = "OwnerOnly",
        });

        var settings = sp.GetRequiredService<WardSettings>();
        Assert.Equal(120, settings.MinStayMinutes);
        Assert.Equal(WardAdmissionMode.OwnerOnly, settings.AdmissionMode);
    }

    // ── Vent ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Vent_settings_are_registered_with_defaults()
    {
        using var sp = BuildProvider();
        var settings = sp.GetRequiredService<VentSettings>();
        Assert.True(settings.RequiredBeforeCard);
        Assert.Equal(VentSettings.InternalGeneralGc, settings.PublishTarget);
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

    // ── GcTypeKeys ────────────────────────────────────────────────────────────

    [Fact]
    public void GcTypeKeys_has_ward_vent_brainstorm_sidewalk_constants()
    {
        Assert.Equal("ward", GcTypeKeys.Ward);
        Assert.Equal("vent", GcTypeKeys.Vent);
        Assert.Equal("brainstorm", GcTypeKeys.Brainstorm);
        Assert.Equal("sidewalk", GcTypeKeys.Sidewalk);
    }

    [Fact]
    public void IsSideWalk_returns_true_for_sidewalk_type_key()
    {
        var gc = new GcRecord(Guid.NewGuid(), null, GcTypeKeys.Sidewalk, null);
        Assert.True(GcTypeKeys.IsSideWalk(gc));
        Assert.False(GcTypeKeys.IsWard(gc));
    }

    [Fact]
    public void IsWard_returns_true_for_ward_type_key()
    {
        var gc = new GcRecord(Guid.NewGuid(), null, GcTypeKeys.Ward, null);
        Assert.True(GcTypeKeys.IsWard(gc));
        Assert.False(GcTypeKeys.IsSideWalk(gc));
    }

    [Fact]
    public void Sidewalk_GC_does_not_match_thinking_or_dm()
    {
        var gc = new GcRecord(Guid.NewGuid(), null, GcTypeKeys.Sidewalk, null);
        Assert.False(GcTypeKeys.IsThinking(gc));
        Assert.False(GcTypeKeys.IsDm(gc));
    }
}
