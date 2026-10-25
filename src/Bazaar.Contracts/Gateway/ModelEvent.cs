using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Gateway;

/// <summary>
/// One event in a streamed model response. Every adapter (OpenAI chat, Anthropic messages, Gemini)
/// translates its wire format into these, so differences stay at the adapter edge.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextDelta), "text_delta")]
[JsonDerivedType(typeof(ThinkingDelta), "thinking_delta")]
[JsonDerivedType(typeof(ToolCallStart), "tool_call_start")]
[JsonDerivedType(typeof(ToolCallArgsDelta), "tool_call_args_delta")]
[JsonDerivedType(typeof(ToolCallEnd), "tool_call_end")]
[JsonDerivedType(typeof(UsageReported), "usage")]
[JsonDerivedType(typeof(Warning), "warning")]
[JsonDerivedType(typeof(Failure), "error")]
[JsonDerivedType(typeof(Done), "done")]
public abstract record ModelEvent
{
    /// <summary>A chunk of visible response text.</summary>
    public sealed record TextDelta(string Text) : ModelEvent;

    /// <summary>A chunk of reasoning text. Counted in output tokens as well.</summary>
    public sealed record ThinkingDelta(string Text) : ModelEvent;

    /// <summary>The model began a tool call.</summary>
    public sealed record ToolCallStart(string CallId, string Name) : ModelEvent;

    /// <summary>A fragment of a tool call's argument JSON. Fragments concatenate in order.</summary>
    public sealed record ToolCallArgsDelta(string CallId, string ArgumentsFragment) : ModelEvent;

    /// <summary>The tool call's arguments are complete.</summary>
    public sealed record ToolCallEnd(string CallId) : ModelEvent;

    /// <summary>Token counts for the call. Usage may arrive only at stream end.</summary>
    public sealed record UsageReported(GatewayTokenUsage Usage) : ModelEvent;

    /// <summary>A non-fatal notice, such as a long prompt warning. Code is a stable machine-readable name.</summary>
    public sealed record Warning(string Code, string Message) : ModelEvent;

    /// <summary>
    /// A failure. Code is a stable name such as <c>rate_limited</c>, <c>spend_capped</c>, <c>timeout</c>,
    /// <c>malformed_tool_json</c>, <c>stream_dropped</c> or <c>provider_error</c>.
    /// </summary>
    public sealed record Failure(string Code, string Message, int? RetryAfterSeconds = null) : ModelEvent;

    /// <summary>The response finished. ServedModel is the model the upstream says it used.</summary>
    public sealed record Done(StopReason StopReason, string ServedModel) : ModelEvent;
}

/// <summary>Why the model stopped generating.</summary>
public enum StopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    ContentFilter,
    Other,
}
