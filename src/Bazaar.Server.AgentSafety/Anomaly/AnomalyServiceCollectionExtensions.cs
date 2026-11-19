using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.Anomaly;

public static class AnomalyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the anomaly detection (doc 48 section 5.6): the default <see cref="AnomalyRuleOptions"/>, the four shipped rules, the
    /// engine and the alert channel. Options and the channel use TryAdd, so a registration made before this call wins. The rules are
    /// added as a collection, so call this once.
    /// </summary>
    public static IServiceCollection AddBazaarAnomalyDetection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => new AnomalyRuleOptions().Validate());
        services.TryAddSingleton(_ => new ChannelAnomalyAlertSource());
        services.TryAddSingleton<IAnomalyAlertSource>(sp => sp.GetRequiredService<ChannelAnomalyAlertSource>());
        services.TryAddSingleton<IAnomalyAlertSink>(sp => sp.GetRequiredService<ChannelAnomalyAlertSource>());

        services.AddSingleton<IAnomalyRule>(sp => new ReadBurstRule(sp.GetRequiredService<AnomalyRuleOptions>().ReadBurst));
        services.AddSingleton<IAnomalyRule>(sp => new OutOfScopeReadRule(sp.GetRequiredService<AnomalyRuleOptions>().OutOfScopeRead));
        services.AddSingleton<IAnomalyRule>(sp => new CanaryHitRule(sp.GetRequiredService<AnomalyRuleOptions>().CanaryHit));
        services.AddSingleton<IAnomalyRule>(sp => new ApprovalFloodRule(sp.GetRequiredService<AnomalyRuleOptions>().ApprovalFlood));

        services.TryAddSingleton(sp => new AnomalyEngine(
            sp.GetRequiredService<IEnumerable<IAnomalyRule>>(),
            sp.GetRequiredService<IAnomalyAlertSink>(),
            sp.GetRequiredService<ILogger<AnomalyEngine>>()));

        return services;
    }
}
