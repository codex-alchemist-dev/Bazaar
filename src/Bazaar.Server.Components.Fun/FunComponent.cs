using Bazaar.Server.Components;
using Bazaar.Server.Fun.Vent;
using Bazaar.Server.Sanitization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Fun;

/// <summary>
/// The fun systems (docs/design/26-fun-systems.md, docs/design/BAZAAR-COMPONENTS.md). Registers the pure logic that needs
/// services: vent drafting uses the shared sanitizer. The Psych Ward state machine and the Lucky controller take their
/// settings directly. Stores (<c>ward_stay</c>, <c>vent</c>) and the settings binding land with their migrations.
/// TODO(component): bind <c>ward.*</c>, <c>vent.*</c> and <c>lucky.*</c> from configuration once the settings registry (doc 03) is wired.
/// </summary>
public sealed class FunComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "fun-systems",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:fun-systems"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<ITextSanitizer, TextSanitizer>();
        services.TryAddSingleton<VentDrafter>();
    }
}
