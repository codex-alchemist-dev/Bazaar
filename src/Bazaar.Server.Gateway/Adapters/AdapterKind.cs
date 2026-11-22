namespace Bazaar.Server.Gateway.Adapters;

/// <summary>The three wire families a provider can speak (docs/design/05-model-gateway.md).</summary>
public enum AdapterKind
{
    OpenAiChat,
    AnthropicMessages,
    GeminiGenerateContent,
}
