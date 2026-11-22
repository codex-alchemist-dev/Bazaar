using Bazaar.Core;
using Bazaar.Server.Fun.Lucky;
using Bazaar.Server.Fun.Sidewalk;
using Bazaar.Server.Fun.Vent;
using Bazaar.Server.Fun.Ward;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Fun;

/// <summary>
/// The fun systems (docs/design/26-fun-systems.md, docs/design/BAZAAR-COMPONENTS.md). Registers the pure logic that needs
/// services: vent drafting uses the shared sanitizer. Vent settings are bound from host configuration. The Psych Ward state
/// machine is configured from the host settings. Stores (<c>ward_stay</c>, <c>vent</c>) and the remaining fun-system settings
/// land with their migrations.
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
        services.TryAddSingleton<VentDrafter>();
        services.AddOptions<VentSettings>()
            .BindConfiguration(VentSettings.SectionName)
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.PublishTarget), "Vent publish target is required.");
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<VentSettings>>().Value);
        services.AddOptions<WardSettings>()
            .BindConfiguration(WardSettings.SectionName)
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            }, "Psych Ward settings are invalid.");
        services.TryAddSingleton<WardStateMachine>(sp =>
            new WardStateMachine(sp.GetRequiredService<IOptions<WardSettings>>().Value));

        // Lucky (doc 26, section 6): settings from Bazaar:Lucky, validated; the controller is a singleton.
        var luckySettings = configuration.GetSection("Bazaar:Lucky").Get<LuckySettings>() ?? new LuckySettings();
        luckySettings.Validate();
        services.TryAddSingleton(luckySettings);
        services.TryAddSingleton<LuckyController>();

        // Sidewalk (doc 26, section 8): sidewalk.daily_token_cap from Bazaar:Sidewalk, validated.
        var sidewalkSettings = configuration.GetSection(SidewalkSettings.SectionName).Get<SidewalkSettings>()
            ?? new SidewalkSettings();
        sidewalkSettings.Validate();
        services.TryAddSingleton(sidewalkSettings);
    }
}
