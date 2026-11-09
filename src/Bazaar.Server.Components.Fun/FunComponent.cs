using Bazaar.Server.Components;
using Bazaar.Server.Fun.Lucky;
using Bazaar.Server.Fun.Sidewalk;
using Bazaar.Server.Fun.Vent;
using Bazaar.Server.Fun.Ward;
using Bazaar.Server.Sanitization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Fun;

/// <summary>
/// The fun systems (docs/design/26-fun-systems.md, docs/design/BAZAAR-COMPONENTS.md). Registers the pure logic that needs
/// services: vent drafting uses the shared sanitizer; the Lucky controller receives settings from configuration. Ward,
/// Vent and Sidewalk settings are bound from <c>Bazaar:Ward</c>, <c>Bazaar:Vent</c> and <c>Bazaar:Sidewalk</c>;
/// Lucky from <c>Bazaar:Lucky</c>. Stores (<c>ward_stay</c>, <c>vent</c>) and migrations land with the host.
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

        // Bind and validate Ward settings.
        var wardSettings = configuration.GetSection("Bazaar:Ward").Get<WardSettings>() ?? new WardSettings();
        wardSettings.Validate();
        services.TryAddSingleton(wardSettings);

        // Bind and validate Vent settings.
        var ventSettings = configuration.GetSection("Bazaar:Vent").Get<VentSettings>() ?? new VentSettings();
        services.TryAddSingleton(ventSettings);

        // Bind and validate Lucky settings; register the controller as a singleton.
        var luckySettings = configuration.GetSection("Bazaar:Lucky").Get<LuckySettings>() ?? new LuckySettings();
        luckySettings.Validate();
        services.TryAddSingleton(luckySettings);
        services.TryAddSingleton<LuckyController>();

        // Bind and validate Sidewalk settings.
        var sidewalkSettings = configuration.GetSection("Bazaar:Sidewalk").Get<SidewalkSettings>() ?? new SidewalkSettings();
        sidewalkSettings.Validate();
        services.TryAddSingleton(sidewalkSettings);
    }
}
