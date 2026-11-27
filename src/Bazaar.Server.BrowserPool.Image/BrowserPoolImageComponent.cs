using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool.Image;

/// <summary>
/// The pool VM image check as a Bazaar Component (docs/design/59, section 11). On Linux it registers a readiness check that the pool
/// consults before every session, so a VM without its programs, directories or <c>bwrap</c> AppArmor profile refuses sessions. On any
/// other OS it registers nothing. It reads the Linux host's options for its paths, so it works whether or not the host component is loaded.
/// </summary>
public sealed class BrowserPoolImageComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool-image",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["browser-pool"],
        Provides: ["browser-pool:image-readiness"]);

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
        PoolImageOptions image = PoolImageOptions.Bind(configuration);
        LinuxPoolHostOptions host = LinuxPoolHostOptions.Bind(configuration);
        services.AddSingleton(image);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPoolImageFileSystem, PhysicalPoolImageFileSystem>();
        services.AddSingleton<IPoolReadinessCheck>(sp =>
        {
            BrowserPoolOptions pool = sp.GetRequiredService<IOptions<BrowserPoolOptions>>().Value;
            return new PoolImageReadinessCheck(
                PoolImageRequirements.From(pool, host, image),
                sp.GetRequiredService<IPoolImageFileSystem>(),
                image,
                sp.GetRequiredService<TimeProvider>());
        });
    }
}
