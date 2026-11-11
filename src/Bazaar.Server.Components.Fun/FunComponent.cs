using Bazaar.Server.Components;
using Bazaar.Server.Fun.Vent;
using Bazaar.Server.Sanitization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Fun;

/// <summary>
/// The fun systems (docs/design/26-fun-systems.md, docs/design/BAZAAR-COMPONENTS.md). Registers the pure logic that needs
/// services: vent drafting uses the shared sanitizer. Vent settings are bound from host configuration; the Psych Ward and
/// Lucky settings remain direct seams until their runtime integrations land.
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
        services.AddOptions<VentSettings>()
            .BindConfiguration(VentSettings.SectionName)
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.PublishTarget), "Vent publish target is required.");
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<VentSettings>>().Value);
    }
}
