using Bazaar.Core;
using Bazaar.Server.Gc.Archive;
using Bazaar.Server.Skimmer.Shadow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gc.Skimmer;

/// <summary>
/// The GC side of the Skimmer shadow gate (doc 13). Off until enabled, and it needs component <c>skimmer</c>. Settings under
/// <c>Bazaar:Skimmer</c>: <c>QueueDepthLimit</c> (200) and <c>BatchSize</c> (50).
/// </summary>
public sealed class GcSkimmerComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gc-skimmer",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["skimmer", "gc"],
        Provides: ["skimmer:gc-source"],
        EnabledByDefault: false);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Bazaar:Skimmer");
        var depth = int.TryParse(section["QueueDepthLimit"], out var d) && d >= 1 ? d : 200;
        var batch = int.TryParse(section["BatchSize"], out var b) && b >= 1 ? b : 50;
        services.AddSingleton(sp => new GcShadowConsumerOptions(sp.GetRequiredService<ShadowSettings>(), depth, batch));
        services.AddGcArchiveBridge();
        services.AddGcShadowConsumers();
    }
}
