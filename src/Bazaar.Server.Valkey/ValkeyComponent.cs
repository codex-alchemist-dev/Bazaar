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
    /// Tags shown in the module manager (doc 42 section 8.1). The strings match <c>DependencyManifest.RequiredTags</c> and
    /// <c>DependencyManifest.ThirdPartyTag</c> in Bazaar.Server, which this module must not reference.
    /// </summary>
    public static IReadOnlyList<string> Tags { get; } = ["Deps", "3rd party"];

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
