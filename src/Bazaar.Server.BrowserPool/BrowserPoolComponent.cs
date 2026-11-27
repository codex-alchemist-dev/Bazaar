using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool;

/// <summary>
/// The shared browser pool as a Bazaar Component (docs/design/59 section 7, work package 8). Registers the pool with fail-closed
/// defaults for every port: until the host registers the real gate, egress policy and VM host, no browser starts.
/// </summary>
public sealed class BrowserPoolComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["browser-pool:sessions"],
        SoftDependsOn: ["vms", "browser", "security", "audit", "vm-display"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BrowserPoolOptions>()
            .Bind(configuration.GetSection(BrowserPoolOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BrowserPoolOptions>, BrowserPoolOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPoolGate, UnconfiguredPoolGate>();
        services.TryAddSingleton<IPoolEgressPolicy, UnconfiguredPoolEgressPolicy>();
        services.TryAddSingleton<IPoolVmHost, UnconfiguredPoolVmHost>();
        services.TryAddSingleton<IPoolAuditSink, NoOpPoolAuditSink>();
        services.TryAddSingleton<IBrowserPool, BrowserPoolService>();
    }
}
