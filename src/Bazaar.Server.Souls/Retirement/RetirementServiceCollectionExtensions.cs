using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>Dependency-injection registration for durable Soul retirement state.</summary>
public static class RetirementServiceCollectionExtensions
{
    /// <summary>Registers the PostgreSQL retirement ledger when no ledger has been supplied by the host.</summary>
    public static IServiceCollection AddBazaarRetirementLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRetirementLedger, PostgresRetirementLedger>();
        return services;
    }
}
