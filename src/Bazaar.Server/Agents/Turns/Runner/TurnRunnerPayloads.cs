using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns.Runner;

/// <summary>The stored response of a completed model step (<c>turn_step.response</c>). Replay reads it back.</summary>
public sealed record ModelStepPayload(
    string Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    GatewayTokenUsage Usage,
    StopReason StopReason,
    string ServedModel);

/// <summary>The stored response of a completed tool step. <see cref="CallId"/> is the model's id for the call.</summary>
public sealed record ToolStepPayload(string CallId, ToolDispatchOutcome Outcome, string? ResultJson, string? Reason);

/// <summary>The stored response of a completed post step.</summary>
public sealed record PostStepPayload(string GcId);

/// <summary>Source-generated JSON for the step payloads (the codebase convention; no reflection at runtime).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(ModelStepPayload))]
[JsonSerializable(typeof(ToolStepPayload))]
[JsonSerializable(typeof(PostStepPayload))]
[JsonSerializable(typeof(ModelMessage[]))]
[JsonSerializable(typeof(ModelToolDefinition[]))]
public partial class TurnRunnerJson : JsonSerializerContext
{
    /// <summary>Reads a stored step payload. A completed step always has one, so a missing payload is corrupt data.</summary>
    internal static T Read<T>(string? json, JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json ?? throw new InvalidDataException("A completed step has no response."), info)
        ?? throw new InvalidDataException("A stored step response is null.");
}

/// <summary>
/// Request hashes for <c>turn_step.request_hash</c>. A hash names a step, so a resumed turn can find the step again.
/// A tool call's hash includes the model response it came from, so two identical calls in different responses stay apart.
/// </summary>
public static class TurnStepHash
{
    /// <summary>Hash of one model request: the messages and the offered tools.</summary>
    public static string ModelRequest(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ModelToolDefinition> tools) => Of(
        JsonSerializer.Serialize(messages.ToArray(), TurnRunnerJson.Default.ModelMessageArray),
        JsonSerializer.Serialize(tools.ToArray(), TurnRunnerJson.Default.ModelToolDefinitionArray));

    /// <summary>Hash of one tool call, scoped to the model response (1-based count of responses in the turn) that made it.</summary>
    public static string ToolCall(int modelResponse, ModelToolCall call) =>
        Of(modelResponse.ToString(CultureInfo.InvariantCulture), call.Id, call.Name, call.ArgumentsJson);

    /// <summary>Hash of one post.</summary>
    public static string Post(string gcId, string text) => Of(gcId, text);

    /// <summary>SHA-256 over the parts, each length-prefixed so that different splits of the same text differ.</summary>
    public static string Of(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
            builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
