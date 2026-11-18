using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Valkey;

/// <summary>
/// The Valkey cache and presence provider as a Bazaar Component (docs/design/42-deps-as-modules.md, BAZAAR-COMPONENTS.md section 2).
/// It is a dependency module: tagged <c>Deps</c> and <c>3rd party</c> (doc 42 sections 3 and 8.1). Cache only; GC fan-out stays on
/// Postgres NOTIFY (doc 04, C07). It depends on no other component.
/// </summary>
public sealed class ValkeyComponent : IBazaarComponent
{
    /// <summary>
    /// Tags shown in the module manager (doc 42 section 8.1). Built from <see cref="DependencyTags"/>, the one source in this module.
    /// <see cref="ComponentManifest"/> has no tags field yet, so the tags live here instead of in the manifest (a gap to close in Bazaar.Core).
    /// TODO: move these tags into the planned <c>ComponentManifest</c> Tags field (doc 40 section 4b.4 item 7, module-system gate #769).
    /// </summary>
    public static IReadOnlyList<string> Tags { get; } = [DependencyTags.Deps, DependencyTags.ThirdParty];

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "valkey",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["cache:valkey"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarValkey(configuration);
}
