using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gateway.Adapters.Anthropic;

/// <summary>The Anthropic Messages wire-family adapter as a Bazaar Component (docs/design/BAZAAR-COMPONENTS.md).</summary>
public sealed class AnthropicMessagesAdapterComponent : IBazaarComponent
{
    /// <summary>The adapter name stored on provider rows.</summary>
    public const string WireName = "anthropic_messages";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "adapter-anthropic-messages",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: [],
        Provides: ["gateway:adapter:" + WireName]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ModelAdapterRegistration(
            WireName,
            sp => new AnthropicMessagesAdapter(sp.GetRequiredService<HttpClient>())));
    }
}
