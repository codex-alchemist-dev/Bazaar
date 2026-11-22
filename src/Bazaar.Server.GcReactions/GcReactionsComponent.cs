using Bazaar.Core;
using Bazaar.Server.GcReactions.Abstractions;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// GC reactions and the typing signal as a Bazaar Component (docs/design/01 "Reactions", docs/design/02 "Reactions on the wire" and
/// "Typing signal"). It adds three wire handlers (<c>gc.react</c>, <c>gc.reactions</c>, <c>gc.typing</c>) over the GC log, which it
/// only reads and appends to through the log's own interfaces. Its SQL is the <c>gc_reactions</c> migration namespace, embedded in
/// this assembly under <c>Sql/components/gc_reactions</c>. Limits bind from <c>Bazaar:GcReactions</c>.
/// </summary>
public sealed class GcReactionsComponent : IBazaarComponent
{
    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "gc_reactions";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gc-reactions",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:gc-reactions", "feature:gc-typing"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(GcReactionsOptionsReader.Read(configuration.GetSection(GcReactionsOptions.SectionName)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGcReactionStore>(sp => new PgGcReactionStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new SlidingRateLimiter(sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(1)));
        services.TryAddSingleton(sp => new TypingTracker(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new GcLiveBroadcaster(
            sp.GetRequiredService<IWireConnectionDirectory>(),
            sp.GetService<SyncOptions>() ?? new SyncOptions(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IWireRequestHandler, GcReactHandler>();
        services.AddSingleton<IWireRequestHandler, GcReactionsHandler>();
        services.AddSingleton<IWireRequestHandler, GcTypingHandler>();
    }
}
