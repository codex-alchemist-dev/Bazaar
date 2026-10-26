using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Gc.Wire;

/// <summary>
/// Glues the wire layer to the GC log (v0.3 GLUE): the stream source, access policy and notification source over the log,
/// the body codec seam, the <c>subscribe</c>, <c>gc.send</c> and <c>gc.history</c> handlers, and the dispatcher's host service.
/// </summary>
/// <remarks>
/// Needs an <c>NpgsqlDataSource</c> registered, and <see cref="SubscriptionEngine"/> and <see cref="GcDispatcher"/> from
/// <c>AddBazaarSync</c>. Call it after <c>AddBazaarWire</c>. It registers the GC log itself (<c>AddGcLog</c>) unless the host
/// already did.
/// </remarks>
public static class GcWireServiceCollectionExtensions
{
    /// <summary>Registers the GC side of the wire. Safe to call once per host.</summary>
    public static IServiceCollection AddBazaarGcWire(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IGcLogWriter)))
            services.AddGcLog();

        services.TryAddSingleton<GcReadAccess>();
        services.TryAddSingleton<IGcReadAccess>(sp => sp.GetRequiredService<GcReadAccess>());

        // INTERIM: plaintext bodies until the per-person key codec lands (docs/design/04 Question 2).
        services.TryAddSingleton<IGcBodyCodec, PlaintextGcBodyCodec>();

        services.TryAddSingleton<PgGcStreamSource>();
        services.TryAddSingleton<IGcStreamSource>(sp => sp.GetRequiredService<PgGcStreamSource>());
        services.TryAddSingleton<IGcAccessPolicy>(sp => new GcStreamAccessPolicy(sp.GetRequiredService<IGcReadAccess>()));
        services.TryAddSingleton<IGcNotificationSource>(sp => new GcNotificationSource(sp.GetRequiredService<GcNotifyListener>()));
        services.AddSingleton<IHostedService, GcDispatcherService>();

        services.AddSingleton<IWireRequestHandler, SubscribeHandler>();
        services.AddSingleton<IWireRequestHandler, GcSendHandler>();
        services.AddSingleton<IWireRequestHandler, GcHistoryHandler>();
        return services;
    }
}
