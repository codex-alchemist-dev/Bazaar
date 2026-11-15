using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Gateway.AnthropicDirect;

/// <summary>
/// The Anthropic pieces the host composes. Each factory returns the unchanged inner piece when the source is disabled, so a
/// disabled config adds no provider and reads no secret.
/// </summary>
public static class AnthropicDirectWiring
{
    public static IGatewayDirectory CreateDirectory(IGatewayDirectory inner, AnthropicDirectOptions options, TimeProvider time) =>
        options.Enabled ? new AnthropicDirectGatewayDirectory(inner, options, time) : inner;

    public static IProviderSecretSource CreateProviderSecretSource(AnthropicDirectOptions options, ISecretResolver resolver, IProviderSecretSource fallback) =>
        options.Enabled ? new AnthropicDirectSecretSource(options.ApiKey, resolver, fallback) : fallback;
}
