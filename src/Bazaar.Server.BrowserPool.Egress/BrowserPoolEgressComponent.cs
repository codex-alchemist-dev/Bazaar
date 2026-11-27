using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmEgress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.BrowserPool.Egress;

/// <summary>
/// Joins the browser pool to the per-agent egress proxy as a Bazaar Component (docs/design/59, "Browser pool isolation";
/// docs/design/16, WP9). It registers one <see cref="AllowlistStorePoolEgressPolicy"/> as the <see cref="IPoolEgressPolicy"/>,
/// replacing the pool's fail-closed default whichever component is added first. The allowlist store is resolved when the policy is
/// first needed; without one no agent has a proxy.
/// </summary>
public sealed class BrowserPoolEgressComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool-egress",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["browser-pool"],
        Provides: ["browser-pool:egress"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(PoolEgressOptions.Bind(configuration));
        services.TryAddSingleton(TimeProvider.System);

        // Replace, not TryAdd: the pool registers its default with TryAdd, so a replace works in either order, and a later host
        // registration still wins.
        services.Replace(ServiceDescriptor.Singleton<IPoolEgressPolicy>(sp => new AllowlistStorePoolEgressPolicy(
            sp.GetService<IEgressAllowlistStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PoolEgressOptions>())));
    }
}
