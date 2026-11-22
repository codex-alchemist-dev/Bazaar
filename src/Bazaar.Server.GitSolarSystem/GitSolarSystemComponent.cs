using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GitSolarSystem;

/// <summary>
/// The Git Solar System server module as a Bazaar Component (doc 19, Git Solar System). It owns no migration namespace and
/// has no dependencies; the feed route is mapped by the host composition.
/// </summary>
public sealed class GitSolarSystemComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "git-solar-system",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:git-solar-system"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddBazaarGitSolarSystem();
    }
}
