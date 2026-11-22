namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// The source label a usage row carries (docs/design/05-model-gateway.md, Claude lane): the owner's Anthropic key, FreeLLMAPI
/// or the Claude Code CLI. Any other provider is labelled by its own id, and a call refused by policy before any provider
/// was chosen is labelled <see cref="Policy"/>.
/// </summary>
public static class UsageSource
{
    public const string AnthropicKey = "anthropic_key";
    public const string FreeLlmApi = "freellmapi";
    public const string ClaudeCodeCli = "claude_code_cli";
    public const string Policy = "policy";

    /// <summary>The provider id on a denied row when no provider could be named.</summary>
    public const string PolicyProviderId = "policy";

    public static string For(string providerId) => providerId switch
    {
        "anthropic-direct" => AnthropicKey,
        "freellmapi" => FreeLlmApi,
        "claude-code-cli" => ClaudeCodeCli,
        PolicyProviderId => Policy,
        _ => providerId,
    };
}
