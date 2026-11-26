using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.VmTransport;

/// <summary>Registration of the guest channel opener (doc 16 "Channel opener", PROPOSED).</summary>
public static class GuestChannelOpenerRegistration
{
    /// <summary>
    /// Registers <see cref="GuestChannelOpener"/> as a hosted service, but only when everything it needs is already registered: an
    /// <see cref="IGuestOpenTargets"/>, a listener, and the channel factory, registry and registrar that <see cref="VmTransportRegistration.AddBazaarVmTransport"/>
    /// adds. Call it after those. When any is missing nothing is registered (fail closed), so a host that cannot say which VMs run and with what
    /// nonce behaves as it did before. An <see cref="IGuestPeerIdResolver"/> is used when registered. The options are TryAdd, so a host can set its own first.
    /// </summary>
    public static IServiceCollection AddBazaarVmGuestOpener(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!Has<IGuestOpenTargets>(services)
            || !Has<IGuestConnectionListener>(services)
            || !Has<GuestChannelFactory>(services)
            || !Has<GuestChannelRegistry>(services)
            || !Has<GuestChannelRegistrar>(services))
        {
            return services;
        }

        services.TryAddSingleton(new GuestChannelOpenerOptions());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GuestChannelOpener>());
        return services;
    }

    private static bool Has<T>(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(T));
}
