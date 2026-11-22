using Bazaar.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>
/// The Linux pool VM host as a Bazaar Component (docs/design/59, section 7). On Linux it replaces the pool's fail-closed
/// <see cref="IPoolVmHost"/> with one <see cref="LinuxPoolVmHost"/>, also exposed as <see cref="IPoolDevToolsPipes"/>. On any other
/// OS it registers nothing, so the pool keeps its fail-closed default. The module may load before or after the pool: it removes
/// any earlier registration of the two ports, and the pool only adds its default when none exists.
/// </summary>
public sealed class BrowserPoolLinuxHostComponent : IBazaarComponent
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool-linux-host",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["browser-pool"],
        Provides: ["browser-pool:linux-host"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        AddServices(services, configuration, OperatingSystem.IsLinux());

    internal static void AddServices(IServiceCollection services, IConfiguration configuration, bool isLinux)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!isLinux)
        {
            return;
        }

        // Bound once, at startup, so a bad setting stops the host rather than the first browser.
        LinuxPoolHostOptions options = LinuxPoolHostOptions.Bind(configuration);
        services.AddSingleton(options);
        services.RemoveAll<IPoolVmHost>();
        services.RemoveAll<IPoolDevToolsPipes>();
        services.AddSingleton(sp =>
        {
            BrowserPoolOptions pool = sp.GetRequiredService<IOptions<BrowserPoolOptions>>().Value;
            return new LinuxPoolVmHost(rules => NftablesRules.Render(rules, pool), options);
        });
        services.AddSingleton<IPoolVmHost>(sp => sp.GetRequiredService<LinuxPoolVmHost>());
        services.AddSingleton<IPoolDevToolsPipes>(sp => sp.GetRequiredService<LinuxPoolVmHost>());
    }
}
