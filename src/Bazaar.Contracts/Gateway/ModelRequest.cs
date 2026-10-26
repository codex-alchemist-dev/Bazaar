namespace Bazaar.Contracts.Gateway;

/// <summary>
/// One model call submitted by the agent runtime to the gateway (docs/design/05-model-gateway.md).
/// <c>Route</c> is a model id or a route name; the gateway resolves it to policy-allowed candidates.
/// </summary>
public sealed record ModelRequest(
    string AgentId,
    string Route,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ModelToolDefinition> Tools,
    string? TurnId = null,
    int? MaxOutputTokens = null,
    double? Temperature = null);

/// <summary>The speaker of a conversation message.</summary>
public enum ModelRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// A conversation message. An assistant message may carry tool calls; a tool message names the call it answers.
/// </summary>
public sealed record ModelMessage(
    ModelRole Role,
    string Text,
    string? ToolCallId = null,
    IReadOnlyList<ModelToolCall>? ToolCalls = null);

/// <summary>
/// A tool call the model made, with its arguments as the raw JSON the model produced.
/// <c>ProviderData</c> is an opaque provider-specific token (for example Gemini's thought signature) that the
/// adapter that produced it sends back unchanged on the next turn. Other code must not interpret it.
/// </summary>
public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson, string? ProviderData = null);

/// <summary>A tool the model may call. The parameters are a JSON Schema document, kept as text.</summary>
public sealed record ModelToolDefinition(string Name, string Description, string ParametersJsonSchema);
