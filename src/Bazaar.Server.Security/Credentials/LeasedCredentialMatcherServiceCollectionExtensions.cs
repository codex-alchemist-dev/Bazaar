using Bazaar.Secrets;
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

    /// <summary>
    /// Wraps the registered <see cref="ISecretPolicy"/> in <see cref="LeaseMatcherSecretPolicy"/>, so the matcher's read works
    /// without an operator grant (doc 14, "Leased credential values" [PROPOSED]). Call it after the host registers its policy;
    /// with none registered, the wrapper sits over a deny-all policy.
    /// </summary>
    public static IServiceCollection AddLeaseMatcherSecretGrant(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(ISecretPolicy));
        if (existing is not null)
        {
            services.Remove(existing);
        }

        services.AddSingleton<ISecretPolicy>(sp =>
        {
            ISecretPolicy inner = existing switch
            {
                null => DenyAllPolicy.Instance,
                { ImplementationInstance: ISecretPolicy instance } => instance,
                { ImplementationFactory: { } factory } => (ISecretPolicy)factory(sp),
                { ImplementationType: { } type } => (ISecretPolicy)ActivatorUtilities.CreateInstance(sp, type),
                _ => DenyAllPolicy.Instance,
            };
            return new LeaseMatcherSecretPolicy(inner);
        });
        return services;
    }

    private sealed class DenyAllPolicy : ISecretPolicy
    {
        public static DenyAllPolicy Instance { get; } = new();

        public Task<bool> AllowsAsync(string component, SecretScope scope, SecretName? name, SecretPurpose purpose, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
