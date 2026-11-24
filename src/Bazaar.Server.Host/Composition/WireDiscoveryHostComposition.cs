using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Discovery;
using Bazaar.Wire.Abstractions;
using Bazaar.Wire.Schema;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Host side of the wire discovery component (doc 90 sections 6 and 8): the schema source, the floor transport and codec ids, and the
/// bridge from the settings store to the module. The module itself is <see cref="WireDiscoveryComponent"/>, listed in <see cref="HostComponents"/>.
/// </summary>
public static class WireDiscoveryHostComposition
{
    /// <summary>Registers the host pieces the discovery module needs. Safe when the component is disabled: nothing reads these.</summary>
    public static IServiceCollection AddBazaarHostWireDiscovery(this IServiceCollection services)
    {
        services.TryAddSingleton<IWireSchemaSource, EmbeddedWireSchemaSource>();
        services.AddSingleton<IWireDiscoveryContribution, FloorWireDiscoveryContribution>();
        services.TryAddSingleton<IWireDiscoverySettingsReader, SettingStoreWireDiscoveryReader>();
        return services;
    }
}

/// <summary>
/// The two floor ids every install serves: transport <c>ws</c> and codec <c>json</c> (doc 90 sections 1 and 2). They are always on and no
/// setting removes them. TODO(#839): the WebSocket server and the JSON codec still live in Bazaar.Server and Bazaar.Contracts. When
/// the Wire extraction lands, each owning module registers its own contribution and this class goes away.
/// </summary>
internal sealed class FloorWireDiscoveryContribution : IWireDiscoveryContribution
{
    public IReadOnlyList<string> Transports { get; } = ["ws"];

    public IReadOnlyList<string> Codecs { get; } = ["json"];
}

/// <summary>Reads the discovery keys from <see cref="ISettingStore"/>.</summary>
internal sealed class SettingStoreWireDiscoveryReader(ISettingStore store) : IWireDiscoverySettingsReader
{
    public async Task<WireDiscoverySettingValues> ReadAsync(CancellationToken cancellationToken) => new(
        await store.GetAsync(WireDiscoverySettings.Enabled, cancellationToken: cancellationToken).ConfigureAwait(false),
        await store.GetAsync(WireDiscoverySettings.PermitLimit, cancellationToken: cancellationToken).ConfigureAwait(false),
        await store.GetAsync(WireDiscoverySettings.WindowSeconds, cancellationToken: cancellationToken).ConfigureAwait(false));
}
