using Bazaar.Server.Agents.Data;
using Bazaar.Server.Setup.FirstRun;
using Bazaar.Server.Setup.FirstRun.Postgres;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Wires the first-run seed (docs/design/45-onboarding.md section 1.15): the <c>firstrun.seed</c> apply step, the Postgres-backed
/// ports behind it and the Agents first-run seams. Needs <c>AddBazaarGateway</c>, which supplies the shared data source, the
/// default-agent routes and the policy grant.
/// </summary>
public static class FirstRunCompositionExtensions
{
    /// <summary>Registers the seed step and its stores. Safe to call once, after <c>AddBazaarGateway</c>.</summary>
    public static IServiceCollection AddBazaarFirstRun(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentFirstRunSeeds>(sp => new PgAgentFirstRunSeeds(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetService<IAgentDefaultPolicyGrant>()));
        services.AddFirstRunPostgres();
        services.AddFirstRunSeed();
        return services;
    }
}
