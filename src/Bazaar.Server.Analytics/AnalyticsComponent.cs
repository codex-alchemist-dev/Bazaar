using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Analytics;

/// <summary>
/// The company analytics module (doc 25, Health and Analytics): per-agent metrics framed as improving the company. The visibility
/// setting is <c>Bazaar:Analytics:AgentMetricsVisibility</c> (<c>Owner</c>, <c>OwnerAndSelf</c>, <c>Peers</c>). A bad value falls
/// back to the narrower default. The module reads work events only; it has no write path into reputation, the Work-Life schedule,
/// votes or Soul state, and a build-time test keeps it so.
/// </summary>
public sealed class AnalyticsComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "analytics",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["analytics:agent-metrics"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => ReadOptions(configuration));
        services.TryAddSingleton<IWorkEventSource, CompositeWorkEventSource>();
        services.AddSingleton<IAgentAnalytics, AgentAnalyticsService>();
    }

    public static AnalyticsOptions ReadOptions(IConfiguration configuration)
    {
        var raw = configuration["Bazaar:Analytics:AgentMetricsVisibility"];
        return Enum.TryParse<AnalyticsVisibility>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? new AnalyticsOptions(v)
            : new AnalyticsOptions();
    }
}
