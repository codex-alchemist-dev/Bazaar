using Cdax.Core;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Presence.Sources;

/// <summary>
/// The presence event sources as a Bazaar Component (docs/design/23, "Event sources and subscription"). It registers one adapter per
/// source, each behind the port the source module calls: availability (doc 09), turns and tools (doc 06), quota (doc 05), Kanban
/// waits (doc 12) and the Psych Ward (doc 26). The adapters feed <see cref="Abstractions.IActivityProjector"/> from the presence
/// component, which it depends on. Nothing happens until a source module calls its port.
/// </summary>
public sealed class PresenceSourcesComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "presence-sources",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["presence"],
        Provides: ["feature:presence-sources"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarPresenceSources();
}

/// <summary>Registers the presence sources.</summary>
public static class PresenceSourcesServiceCollectionExtensions
{
    /// <summary>Adds the five adapters, the restart seed source and the default tool classifier. Each can be replaced by registering the port first.</summary>
    public static IServiceCollection AddBazaarPresenceSources(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new PresenceSourcesOptions());
        services.TryAddSingleton<IGcKindLookup>(sp => new GcKindLookup(
            sp.GetService<Bazaar.Server.GcMetadata.IGcHeaderSource>(), sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(60)));
        services.TryAddSingleton<IPresenceToolClassifier, NameTableToolClassifier>();
        services.TryAddSingleton<IAvailabilityTransitionSink, AvailabilityPresenceSource>();
        services.TryAddSingleton<ITurnActivitySink, TurnPresenceSource>();
        services.TryAddSingleton<QuotaPresenceSource>();
        services.TryAddSingleton<IQuotaCapacitySink>(sp => sp.GetRequiredService<QuotaPresenceSource>());
        services.TryAddSingleton<IKanbanWaitSink, KanbanPresenceSource>();
        services.TryAddSingleton<IWardStateSink, WardPresenceSource>();
        // The restart seed reads whichever of the three readers the host registers; the hosted service finds it by its port.
        services.TryAddSingleton<IPresenceSeedSource>(sp => new ReadersPresenceSeedSource(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IAvailabilityStateReader>(),
            sp.GetService<IAgentScheduleReader>(),
            sp.GetService<IOpenTurnReader>()));
        return services;
    }
}
