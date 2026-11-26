using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Observatory;

/// <summary>
/// The Observatory module (doc 23): usage by agent, model and source, GC cost chips, lane near-limit state and the dashboard card.
/// The host supplies <see cref="IUsageReader"/>, <see cref="IUsageCostCalculator"/> and <see cref="ILaneReader"/> from the gateway's tables.
/// Settings: <c>Bazaar:Observatory:NearLimitPercent</c>, <c>:LongPromptTokens</c>, <c>:EstimateLabel</c>.
/// </summary>
public sealed class ObservatoryComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "observatory",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["observatory:usage", "observatory:cost-chip", "observatory:lanes"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => ReadOptions(configuration));
        services.AddSingleton<IObservatoryService, ObservatoryService>();
    }

    public static ObservatoryOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Bazaar:Observatory");
        var defaults = new ObservatoryOptions();
        var percent = int.TryParse(section["NearLimitPercent"], out var p) ? p : defaults.NearLimitPercent;
        if (percent is < 1 or > 100)
        {
            throw new InvalidOperationException("Bazaar:Observatory:NearLimitPercent must be 1 to 100.");
        }

        return new ObservatoryOptions
        {
            NearLimitPercent = percent,
            LongPromptTokens = long.TryParse(section["LongPromptTokens"], out var l) && l >= 0 ? l : defaults.LongPromptTokens,
            EstimateLabel = !bool.TryParse(section["EstimateLabel"], out var e) || e,
        };
    }
}
