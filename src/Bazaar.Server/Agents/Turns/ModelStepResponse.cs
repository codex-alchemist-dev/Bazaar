using System.Text.Json.Serialization;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// The response stored in <c>turn_step.response</c> for one model call: the text, the stop reason and any tool calls the
/// model made. Tool calls are recorded but not dispatched here (WP6). Written before the turn acts on the response.
/// </summary>
public sealed record ModelStepResponse(
    string Text,
    string StopReason,
    string ServedModel,
    IReadOnlyList<ModelStepToolCall> ToolCalls,
    long Tokens);

/// <summary>One tool call the model asked for, with its arguments as the JSON the model produced.</summary>
public sealed record ModelStepToolCall(string CallId, string Name, string ArgumentsJson);

/// <summary>Source-generated JSON for <see cref="ModelStepResponse"/>; no reflection on the read or write path.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(ModelStepResponse))]
internal sealed partial class TurnJsonContext : JsonSerializerContext
{
}
