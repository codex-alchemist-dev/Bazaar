using Bazaar.Server.VmTransport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>The host OS family the enforcer is chosen for.</summary>
internal enum EnforcerPlatform
{
    /// <summary>No enforcer exists for this OS; egress stays fail closed.</summary>
    Unsupported,

    /// <summary>Hyper-V extended ACLs.</summary>
    Windows,

    /// <summary>nftables bridge table.</summary>
    Linux,
}

/// <summary>
/// Composition for the real <see cref="IEgressEnforcer"/> implementations (doc 16 WP9, layer 1; PROPOSED). The host registers an
/// <see cref="IEgressTargetResolver"/>; without one the enforcer is the <see cref="UnavailableEgressEnforcer"/>, which throws at
/// call time, so egress is never reported as in place.
/// </summary>
public static class EgressEnforcersModule
{
    /// <summary>
    /// Replaces the enforcer with the Hyper-V one on Windows or the nftables one on Linux. Order-independent with
    /// <c>AddBazaarVmEgress</c>, which only adds its unavailable default when none is registered.
    /// </summary>
    public static IServiceCollection AddBazaarVmEgressEnforcers(this IServiceCollection services) =>
        services.AddBazaarVmEgressEnforcers(DetectPlatform());

    internal static IServiceCollection AddBazaarVmEgressEnforcers(this IServiceCollection services, EnforcerPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHostCommandRunner>(_ => new ProcessHostCommandRunner());
        services.TryAddSingleton<IRootCommandRunner>(sp => new NftRootCommandRunner(sp.GetRequiredService<IHostCommandRunner>()));
        services.Replace(ServiceDescriptor.Singleton<IEgressEnforcer>(sp =>
        {
            // An explicit resolver wins. Otherwise the VM store port, when the host composed one (the transport module's default knows no VMs,
            // so it fails closed), gives the resolver. Neither: the enforcer stays unavailable.
            var resolver = sp.GetService<IEgressTargetResolver>()
                ?? (sp.GetService<IAgentVmLookup>() is { } lookup ? new LookupEgressTargetResolver(lookup) : null);
            if (resolver is null)
            {
                return new UnavailableEgressEnforcer();
            }

            return platform switch
            {
                EnforcerPlatform.Windows => new HyperVEgressEnforcer(resolver, sp.GetRequiredService<IHostCommandRunner>()),
                EnforcerPlatform.Linux => new NftablesEgressEnforcer(resolver, sp.GetRequiredService<IRootCommandRunner>()),
                _ => new UnavailableEgressEnforcer(),
            };
        }));
        return services;
    }

    // The one OS check in the composition; the enforcers and their call sites have none.
    private static EnforcerPlatform DetectPlatform() =>
        OperatingSystem.IsWindows() ? EnforcerPlatform.Windows
        : OperatingSystem.IsLinux() ? EnforcerPlatform.Linux
        : EnforcerPlatform.Unsupported;
}
