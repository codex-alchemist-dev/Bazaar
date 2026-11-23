using Bazaar.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SoulProfile;

/// <summary>
/// The read-only agent profile route as a Bazaar Component (doc 07, PROPOSED). The caller resolver and the profile source come from
/// the modules that own sign-in and Souls; until they are registered the route answers 503, never a guess.
/// </summary>
public sealed class SoulProfileComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "soul-profile",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:soul-profile"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<SoulProfileComponentMarker>();

        // The binding comes from the Souls module's own read side; the other three ports (identity, versions, access) are supplied by
        // the modules that own them, so until they are registered the route answers 503.
        services.TryAddSingleton<ISoulBindingReader, SoulBindingReader>();
    }
}

/// <summary>Maps the profile route for a host that ships <see cref="SoulProfileComponent"/>.</summary>
public static class SoulProfileRouting
{
    public static IEndpointRouteBuilder MapBazaarSoulProfile(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.ServiceProvider.GetService<SoulProfileComponentMarker>() is not null) routes.MapSoulProfileEndpoints();
        return routes;
    }
}

internal sealed class SoulProfileComponentMarker;
