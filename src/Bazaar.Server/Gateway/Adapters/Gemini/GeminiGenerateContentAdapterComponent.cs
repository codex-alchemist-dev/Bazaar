using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gateway.Adapters.Gemini;

/// <summary>The Gemini generateContent wire-family adapter as a Bazaar Component (docs/design/BAZAAR-COMPONENTS.md).</summary>
public sealed class GeminiGenerateContentAdapterComponent : IBazaarComponent
{
    /// <summary>The adapter name stored on provider rows.</summary>
    public const string WireName = "gemini_generate_content";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "adapter-gemini-generate-content",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: [],
        Provides: ["gateway:adapter:" + WireName]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ModelAdapterRegistration(
            WireName,
            sp => new GeminiGenerateContentAdapter(sp.GetRequiredService<HttpClient>())));
    }
}
