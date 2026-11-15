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
/// Glues the wire layer to the GC log (Stage 3 GLUE): the stream source, access policy and notification source over the log,
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

        // Per-person AES-256-GCM bodies (docs/design/04 Question 2). The KEK provider is optional at registration: without one the
        // codec fails closed when it seals, so no body is stored in plaintext (see AesGcmGcBodyCodec).
        services.TryAddSingleton<IGcBodyCodec>(sp => new AesGcmGcBodyCodec(
            sp.GetRequiredService<Npgsql.NpgsqlDataSource>(),
            sp.GetService<Bazaar.Server.Identity.Keys.IKekProvider>()));

        // The operator's settings store. The settings component registers the audited store first; this fallback keeps a GC-only
        // host working (reads need no audit sink; writes only come from the settings component).
        services.TryAddSingleton<Bazaar.Server.Settings.ISettingStore>(sp => new Bazaar.Server.Settings.NpgsqlSettingStore(
            sp.GetRequiredService<Npgsql.NpgsqlDataSource>(),
            sp.GetService<Bazaar.Server.Settings.ISettingAuditSink>() ?? new Bazaar.Server.Settings.InMemorySettingAuditSink()));

        services.TryAddSingleton<Bazaar.Server.Gc.Mentions.IInboxRowWriter>(sp => new Bazaar.Server.Gc.Mentions.InboxRowWriter(
            sp.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        services.TryAddSingleton<PgGcStreamSource>();
        services.TryAddSingleton<IGcStreamSource>(sp => sp.GetRequiredService<PgGcStreamSource>());
        services.TryAddSingleton<IGcAccessPolicy>(sp => new GcStreamAccessPolicy(sp.GetRequiredService<IGcReadAccess>()));
        services.TryAddSingleton<IGcNotificationSource>(sp => new GcNotificationSource(sp.GetRequiredService<GcNotifyListener>()));
        services.AddSingleton<IHostedService, GcDispatcherService>();

        services.AddSingleton<IWireRequestHandler, SubscribeHandler>();
        services.TryAddSingleton<Bazaar.Server.Gc.Edit.PgGcEditSource>();
        services.AddSingleton<IWireRequestHandler, GcSendHandler>();
        services.AddSingleton<IWireRequestHandler, GcEditHandler>();
        services.TryAddSingleton<Bazaar.Server.Gc.Members.PgGcMembershipSource>();
        services.AddSingleton<IWireRequestHandler, GcMemberAddHandler>();
        services.AddSingleton<IWireRequestHandler, GcMemberRemoveHandler>();
        services.AddSingleton<IWireRequestHandler, GcHistoryHandler>();
        return services;
    }
}
