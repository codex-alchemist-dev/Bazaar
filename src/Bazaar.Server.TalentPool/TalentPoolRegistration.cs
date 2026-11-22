using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.TalentPool;

/// <summary>Dependency injection for the Talent Pool.</summary>
public static class TalentPoolRegistration
{
    /// <summary>
    /// Registers <see cref="ITalentPool"/>. The host must register <see cref="ITalentModelWhitelist"/>,
    /// <see cref="ITalentToolGrantApproval"/>, <see cref="ITalentInstantiator"/>, the Council hiring services
    /// (<c>AddBazaarCouncilHiring</c>) and <c>IHireDecisionStore</c>. A store defaults to the in-memory one.
    /// </summary>
    public static IServiceCollection AddBazaarTalentPool(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITalentTemplateStore, InMemoryTalentTemplateStore>();
        services.TryAddSingleton<ITalentPool, TalentPoolService>();
        return services;
    }
}
