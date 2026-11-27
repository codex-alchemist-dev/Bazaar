using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Presence;

/// <summary>
/// The presence projector and publisher as a Bazaar Component (docs/design/23 "Activity and presence", work package 4). It turns
/// availability, turn, quota, Kanban, cover and ward events into one presence row per agent, publishes <c>presence.update</c> deltas
/// with per-agent revisions, and answers <c>presence.snapshot</c>. It uses the wire and the cache provider when they are present and
/// needs neither: with no cache the snapshot cache is skipped, and with no socket the deltas go nowhere. Event sources register as
/// callers of <see cref="Abstractions.IActivityProjector"/>.
/// </summary>
public sealed class PresenceComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "presence",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:presence"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarPresence(configuration);
}
