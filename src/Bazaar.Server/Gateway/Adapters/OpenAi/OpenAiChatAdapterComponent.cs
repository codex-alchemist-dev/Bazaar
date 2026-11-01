using Bazaar.Server.Components;
using Bazaar.Server.Gateway.FreeLlmApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.Adapters.OpenAi;

/// <summary>
/// The OpenAI-compatible chat wire-family adapter as a Bazaar Component (docs/design/05-model-gateway.md,
/// docs/design/BAZAAR-COMPONENTS.md). When FreeLLMAPI is enabled, the adapter is wrapped by its routing layer, as before.
/// </summary>
public sealed class OpenAiChatAdapterComponent : IBazaarComponent
{
    /// <summary>The adapter name stored on provider rows.</summary>
    public const string WireName = "openai_chat";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "adapter-openai-chat",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: [],
        Provides: ["gateway:adapter:" + WireName]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ModelAdapterRegistration(WireName, CreateAdapter));
    }

    private static IModelAdapter CreateAdapter(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<HttpClient>();
        var time = sp.GetRequiredService<TimeProvider>();
        var freeLlmApi = sp.GetRequiredService<IOptions<FreeLlmApiOptions>>().Value;
        var freeLlmApiHttp = freeLlmApi.Enabled ? FreeLlmApiWiring.CreateHttpClient(freeLlmApi) : http;
        return FreeLlmApiWiring.WrapOpenAiAdapter(
            new OpenAiChatAdapter(http, time),
            freeLlmApi,
            freeLlmApiHttp,
            time,
            sp.GetRequiredService<FreeLlmApiRoutingDiagnostics>());
    }
}
