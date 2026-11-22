using Bazaar.Server.Identity.Leases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security.Credentials;

/// <summary>Registers the leased-credential matcher and the observer that feeds it from credential leases.</summary>
public static class LeasedCredentialMatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryLeasedCredentialMatcher"/> (also as <see cref="ILeasedCredentialMatcher"/>) and
    /// <see cref="LeasedCredentialObserver"/> as an <see cref="ILeaseObserver"/> (doc 15, "Reads for modules").
    /// The host must also register an <see cref="ILeasedCredentialValueSource"/>; without one the observer cannot be built,
    /// so the host fails to start instead of leaving leased values unguarded.
    /// </summary>
    public static IServiceCollection AddLeasedCredentialMatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InMemoryLeasedCredentialMatcher>();
        services.TryAddSingleton<ILeasedCredentialMatcher>(sp => sp.GetRequiredService<InMemoryLeasedCredentialMatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILeaseObserver, LeasedCredentialObserver>());
        return services;
    }

    /// <summary>
    /// Registers <see cref="SecretStoreLeasedCredentialValueSource"/> as the host's <see cref="ILeasedCredentialValueSource"/>
    /// (doc 14, "Leased credential values" [PROPOSED]). Needs an <c>ISecretStore</c> from the host.
    /// </summary>
    public static IServiceCollection AddSecretStoreLeasedCredentialValues(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ILeasedCredentialValueSource, SecretStoreLeasedCredentialValueSource>();
        return services;
    }
}
