using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Lucky;

/// <summary>
/// The I'm Feeling Lucky module (docs/design/26-fun-systems.md, section 6). Settings come from <c>Bazaar:Lucky</c>, validated
/// once. The controller is a singleton and uses the shared sanitizer, which the host supplies.
/// </summary>
public sealed class LuckyComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "lucky",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:lucky"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var luckySettings = configuration.GetSection("Bazaar:Lucky").Get<LuckySettings>() ?? new LuckySettings();
        luckySettings.Validate();
        services.TryAddSingleton(luckySettings);
        services.TryAddSingleton<LuckyController>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LuckyStartService>();
        services.AddSingleton<IWireRequestHandler>(sp => new LuckyStartWireHandler(
            sp.GetRequiredService<LuckyStartService>(), sp.GetRequiredService<TimeProvider>()));
    }
}
