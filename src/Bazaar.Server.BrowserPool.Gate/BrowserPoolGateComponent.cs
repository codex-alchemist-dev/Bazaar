using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.BrowserPool.Gate;

/// <summary>
/// Joins the browser pool to the kill switch as a Bazaar Component (docs/design/59, "Browser pool isolation"; docs/design/15,
/// "Reads for modules"). It registers one <see cref="KillSwitchPoolGate"/> as both the <see cref="IPoolGate"/> and the
/// <see cref="IAsyncPoolGate"/>, replacing the pool's blocks-everyone default whichever component is added first. The kill switch
/// reader is resolved when the gate is first needed; without one every agent stays blocked.
/// </summary>
public sealed class BrowserPoolGateComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool-gate",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["browser-pool"],
        Provides: ["browser-pool:gate"],
        SoftDependsOn: ["security"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(PoolGateOptions.Bind(configuration));
        services.TryAddSingleton(TimeProvider.System);

        // Replace, not TryAdd: the pool registers its default with TryAdd, so a replace works in either order, and a later host
        // registration still wins. Both ports resolve to one instance.
        services.Replace(ServiceDescriptor.Singleton(sp => new KillSwitchPoolGate(
            sp.GetService<IKillSwitchReader>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PoolGateOptions>())));
        services.Replace(ServiceDescriptor.Singleton<IPoolGate>(sp => sp.GetRequiredService<KillSwitchPoolGate>()));
        services.Replace(ServiceDescriptor.Singleton<IAsyncPoolGate>(sp => sp.GetRequiredService<KillSwitchPoolGate>()));
    }
}
