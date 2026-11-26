using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.Tests.Anomaly;

public sealed class AnomalyServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_the_four_shipped_rules_with_their_ids()
    {
        using var provider = BuildProvider();

        var ids = provider.GetServices<IAnomalyRule>().Select(rule => rule.Id).Order().ToArray();

        Assert.Equal(["approval_flood", "canary_hit", "out_of_scope_read", "read_burst"], ids);
    }

    [Fact]
    public void Source_and_sink_resolve_to_the_same_channel()
    {
        using var provider = BuildProvider();

        var source = provider.GetRequiredService<IAnomalyAlertSource>();
        var sink = provider.GetRequiredService<IAnomalyAlertSink>();

        Assert.Same(source, sink);
    }

    [Fact]
    public async Task A_canary_touch_reaches_the_alert_source_through_the_engine()
    {
        using var provider = BuildProvider();
        var engine = provider.GetRequiredService<AnomalyEngine>();
        var source = provider.GetRequiredService<IAnomalyAlertSource>();

        await engine.HandleAsync(
            new AnomalyEvent(AnomalyEventKind.CanaryTouched, "agent-a", "session-1", null, InScope: true, FakeClock.DefaultStart),
            TestContext.Current.CancellationToken);

        await using var enumerator = source.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("canary_hit", enumerator.Current.RuleId);
        Assert.Equal(AnomalySeverity.Critical, enumerator.Current.Severity);
    }

    [Fact]
    public async Task A_registration_made_before_the_call_wins()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AnomalyRuleOptions { CanaryHit = new SeverityRuleOptions(AnomalySeverity.High) });
        services.AddBazaarAnomalyDetection();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<AnomalyEngine>().HandleAsync(
            new AnomalyEvent(AnomalyEventKind.CanaryTouched, "agent-a", null, null, InScope: true, FakeClock.DefaultStart),
            TestContext.Current.CancellationToken);

        var source = provider.GetRequiredService<IAnomalyAlertSource>();
        await using var enumerator = source.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(AnomalySeverity.High, enumerator.Current.Severity);
    }

    [Fact]
    public void Invalid_options_are_rejected_when_the_rules_are_built()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AnomalyRuleOptions { ReadBurst = new WindowedRuleOptions(0, TimeSpan.FromMinutes(1), AnomalySeverity.High) });
        services.AddBazaarAnomalyDetection();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => provider.GetServices<IAnomalyRule>().ToList());
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarAnomalyDetection();
        return services.BuildServiceProvider();
    }
}
