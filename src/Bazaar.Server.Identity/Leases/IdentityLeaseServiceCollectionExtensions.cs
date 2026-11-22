using Bazaar.Server.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Identity.Leases;

/// <summary>Registers the credential lease store and the <see cref="IIdentityService"/> with safe defaults.</summary>
public static class IdentityLeaseServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IdentityOptions"/>, the in-memory lease store, <see cref="NoAccessApprovals"/> and
    /// <see cref="IIdentityService"/> (all with TryAdd). The host must supply <see cref="IAgentStateReader"/>,
    /// <see cref="ICredentialDirectory"/> and <see cref="IIdentityAudit"/>. Nothing is wired into the host here.
    /// </summary>
    public static IServiceCollection AddBazaarIdentityLeases(this IServiceCollection services, Action<IdentityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IdentityOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IdentityOptions>(sp => sp.GetRequiredService<IOptions<IdentityOptions>>().Value);
        services.TryAddSingleton<ICredentialLeaseStore, InMemoryCredentialLeaseStore>();
        services.TryAddSingleton<IAccessApprovalSource, NoAccessApprovals>();
        services.TryAddSingleton<IIdentityService>(sp => new IdentityService(
            sp.GetRequiredService<IdentityOptions>(),
            sp.GetRequiredService<IAgentStateReader>(),
            sp.GetRequiredService<ICredentialDirectory>(),
            sp.GetRequiredService<ICredentialLeaseStore>(),
            sp.GetRequiredService<IIdentityAudit>(),
            sp.GetRequiredService<IAccessApprovalSource>(),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}
