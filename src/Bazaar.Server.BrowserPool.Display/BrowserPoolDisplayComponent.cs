using System.Diagnostics.CodeAnalysis;
using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.BrowserPool.Display;

/// <summary>
/// Joins the display module to the browser pool as a Bazaar Component (docs/design/59, sections 4 and 7). It registers
/// <see cref="PoolBrowserPageDriver"/> as an <see cref="IBrowserPageDriver"/>, which the display module collects with the other
/// drivers. Without a host that hands out DevTools pipes the driver still resolves and supports no target.
/// </summary>
public sealed class BrowserPoolDisplayComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool-display",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["vm-display", "browser-pool"],
        Provides: ["browser-pool:page-driver"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        BrowserPageDriverOptions options = BrowserPageDriverOptions.Bind(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton<IPoolDevToolsPipes, NoDevToolsPipes>();
        services.AddSingleton<IBrowserPageDriver>(sp => new PoolBrowserPageDriver(sp.GetRequiredService<IPoolDevToolsPipes>(), options));
    }

    /// <summary>The stand-in when no host hands out pipes: no session has one.</summary>
    internal sealed class NoDevToolsPipes : IPoolDevToolsPipes
    {
        public bool TryGetPipe(string sessionId, [NotNullWhen(true)] out PoolDevToolsPipe? pipe)
        {
            pipe = null;
            return false;
        }
    }
}
