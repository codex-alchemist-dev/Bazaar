using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// The FreeLLMAPI pieces the host composes. Each factory returns the unchanged inner piece when FreeLLMAPI is disabled, so a
/// disabled config adds no provider, reads no secret and gives no FreeLLMAPI traffic any special handling.
/// </summary>
public static class FreeLlmApiWiring
{
    public static IGatewayDirectory CreateDirectory(IGatewayDirectory inner, FreeLlmApiOptions options, TimeProvider time) =>
        options.Enabled ? new FreeLlmApiGatewayDirectory(inner, options, time) : inner;

    public static IProviderSecretSource CreateProviderSecretSource(FreeLlmApiOptions options, ISecretResolver resolver, IProviderSecretSource fallback) =>
        options.Enabled ? new FreeLlmApiSecretSource(options.ApiKey, resolver, fallback) : fallback;

    /// <summary>The HTTP client for FreeLLMAPI calls. Its timeout is the configured <c>TimeoutSeconds</c>.</summary>
    public static HttpClient CreateHttpClient(FreeLlmApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
    }

    /// <summary>Wraps the openai_chat adapter when enabled; returns <paramref name="standard"/> unchanged otherwise.</summary>
    public static IModelAdapter WrapOpenAiAdapter(
        IModelAdapter standard,
        FreeLlmApiOptions options,
        HttpClient freeLlmApiHttp,
        TimeProvider time,
        FreeLlmApiRoutingDiagnostics diagnostics) =>
        options.Enabled
            ? new FreeLlmApiRoutingAdapter(standard, new OpenAiChatAdapter(freeLlmApiHttp, time), options.BaseUrl, diagnostics)
            : standard;
}
