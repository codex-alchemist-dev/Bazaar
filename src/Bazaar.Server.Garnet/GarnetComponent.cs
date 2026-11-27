using Cdax.Core;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Garnet;

/// <summary>
/// The Garnet cache and presence provider as a Bazaar Component (docs/design/42-deps-as-modules.md, BAZAAR-COMPONENTS.md section 2).
/// It is a dependency module: tagged <c>Deps</c> and <c>3rd party</c> (doc 42 sections 3 and 8.1). Cache only; GC fan-out stays on
/// Postgres NOTIFY (doc 04, C07). It depends on no other component.
/// </summary>
public sealed class GarnetComponent : ICdaxModule
{
    /// <summary>Tags shown in the module manager (doc 42 section 8.1), from the shared <see cref="DependencyTags"/> strings.</summary>
    public static IReadOnlyList<string> Tags { get; } = [DependencyTags.Deps, DependencyTags.ThirdParty];

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "garnet",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["cache:garnet"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarGarnet(configuration);
}
