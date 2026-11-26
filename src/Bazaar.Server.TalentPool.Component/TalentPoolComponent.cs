using Bazaar.Core;
using Bazaar.Server.Council.Hiring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>
/// The Talent Pool as a Bazaar Component (docs/design/25 WP8). SQL is the <c>talent_pool</c> migration namespace, embedded in this
/// assembly. The Council service, the gateway directory, the agent repository and the Soul services come from their own components;
/// resolving <see cref="ITalentPool"/> fails with the missing service's name when one is absent.
/// </summary>
public sealed class TalentPoolComponent : IBazaarComponent
{
    /// <summary>Migration namespace and history key.</summary>
    public const string MigrationProvider = "talent_pool";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "talent-pool",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:talent-pool"],
        SoftDependsOn: []);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITalentTemplateStore>(sp => new PostgresTalentTemplateStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IHireDecisionStore>(sp => new PostgresHireDecisionStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new PostgresToolGrantApproval(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ITalentToolGrantApproval>(sp => sp.GetRequiredService<PostgresToolGrantApproval>());
        services.TryAddSingleton<ITalentModelWhitelist, GatewayTalentModelWhitelist>();
        services.TryAddSingleton<ITalentInstantiator, SoulTalentInstantiator>();
        services.AddBazaarCouncilHiring();
        services.AddBazaarTalentPool();
    }
}
