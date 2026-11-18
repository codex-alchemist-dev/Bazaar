using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.TalentPool;

public static class TalentPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres template store, the Talent Pool service and the fail-closed <see cref="DenyAllTalentHireGate"/> (all TryAdd, so the
    /// Council adapter registered first wins). The host must register an <c>NpgsqlDataSource</c> and an <see cref="ITalentModelWhitelist"/>.
    /// </summary>
    public static IServiceCollection AddBazaarTalentPool(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITalentTemplateStore, PostgresTalentTemplateStore>();
        services.TryAddSingleton<ITalentHireGate, DenyAllTalentHireGate>();
        services.TryAddSingleton<ITalentPool, TalentPoolService>();
        return services;
    }
}
