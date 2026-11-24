using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.EngineRelay;

/// <summary>Registration of the relay wire adapters (doc 02, doc 20).</summary>
public static class EngineRelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the target directory, the publisher and the <c>engine.relay.result</c> and <c>engine.grant.verify</c> handlers. The host supplies
    /// <see cref="IWireConnectionDirectory"/> (the wire kernel), <see cref="EngineRelayBroker"/> and, optionally, <see cref="SyncOptions"/>.
    /// The publisher reaches the broker lazily to tell it when a device's connection ends; the broker needs the publisher, so
    /// resolving it at construction would loop.
    /// </summary>
    public static IServiceCollection AddBazaarEngineRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRelayTargetDirectory, WireRelayTargetDirectory>();
        services.TryAddSingleton<IRelayPublisher>(sp => new WireRelayPublisher(
            sp.GetRequiredService<IWireConnectionDirectory>(),
            deviceId => sp.GetRequiredService<EngineRelayBroker>().DeviceClosed(deviceId),
            sp.GetService<SyncOptions>(),
            sp.GetService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, EngineRelayResultHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, EngineGrantVerifyHandler>());
        return services;
    }
}
