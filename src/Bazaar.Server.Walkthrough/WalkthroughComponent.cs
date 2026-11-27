using Cdax.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Walkthrough;

/// <summary>
/// The first-run walkthrough's server side as a Bazaar Component (doc 45 section 2.9, PROPOSED). It registers the in-memory
/// preference store (a durable store replaces it) and marks the component enabled, so <see cref="WalkthroughRouting.MapBazaarWalkthrough"/>
/// maps the routes. The caller resolver and the facts provider come from the modules that own that knowledge (sign-in tokens, agents
/// and Kanban); until they are registered the routes answer 503, never a guess.
/// </summary>
public sealed class WalkthroughComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "walkthrough",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:walkthrough"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<WalkthroughComponentMarker>();
        // Durable when the host has its database; in memory (a restart forgets) when it does not, such as a test host.
        services.TryAddSingleton<IWalkthroughPrefsStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PostgresWalkthroughPrefsStore(dataSource) : new InMemoryWalkthroughPrefsStore());
    }
}

/// <summary>Maps the walkthrough routes for a host that ships <see cref="WalkthroughComponent"/>.</summary>
public static class WalkthroughRouting
{
    /// <summary>Maps the routes when the component is enabled and nothing otherwise, so a host without it answers 404.</summary>
    public static IEndpointRouteBuilder MapBazaarWalkthrough(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.ServiceProvider.GetService<WalkthroughComponentMarker>() is not null) routes.MapWalkthroughEndpoints();
        return routes;
    }
}

internal sealed class WalkthroughComponentMarker;
