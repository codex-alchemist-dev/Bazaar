using Bazaar.Core;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Wire.Discovery;

/// <summary>
/// The wire discovery route as a Bazaar Component (doc 90 section 6, docs/design/BAZAAR-COMPONENTS.md). Registers the catalog, limiter,
/// settings keys and the live settings refresher. The host still supplies <c>IWireSchemaSource</c>, the transport and codec
/// contributions, and an <see cref="IWireDiscoverySettingsReader"/>; the route is mapped in <c>Program.cs</c> only while the component is enabled.
/// </summary>
public sealed class WireDiscoveryComponent : IBazaarComponent
{
    /// <summary>Component id, also the key under <c>Bazaar:Components</c>.</summary>
    public const string Id = "wire-discovery";

    /// <summary>The manifest.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: Id,
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:wire-discovery"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddBazaarWireDiscovery();
        services.AddSingleton<ISettingContribution, WireDiscoverySettingContribution>();
        services.TryAddSingleton<WireDiscoveryLiveSettings>();
        services.AddHostedService<WireDiscoverySettingsRefresher>();
    }
}
