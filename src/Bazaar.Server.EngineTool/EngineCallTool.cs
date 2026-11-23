using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.EngineDispatch;

namespace Bazaar.Server.EngineTool;

/// <summary>
/// The agent-facing engine tool <c>engine.call</c> (doc 20, Behaviour; PROPOSED). It names one engine tool call for the calling
/// agent: the caller is the dispatcher's <see cref="ToolCallContext"/>, never an argument, and the group chat the call is recorded
/// in is <see cref="ToolCallContext.GcId"/>. The call goes to <see cref="EngineRelayBroker"/>, which authorises it (grants, taint,
/// allowlist, audit, GC record) before anything reaches the device. Results are shaped as one small JSON document; a device
/// answer larger than <see cref="MaxResultBytes"/> is reported by size instead of being passed on.
/// </summary>
public sealed class EngineCallTool(EngineRelayBroker broker) : IContextualTool
{
    /// <summary>The tool name the model calls.</summary>
    public const string ToolName = "engine.call";

    /// <summary>Largest device answer passed to the model, in UTF-8 bytes.</summary>
    public const int MaxResultBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (args.ValueKind != JsonValueKind.Object
            || !TryString(args, "adapter_id", out var adapter)
            || !TryString(args, "studio_id", out var studio)
            || !TryString(args, "tool", out var tool))
        {
            return Refusal("bad_arguments", "adapter_id, studio_id and tool are required strings");
        }

        string? grantId = null;
        if (args.TryGetProperty("grant_id", out var grant))
        {
            if (grant.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(grant.GetString()))
                return Refusal("bad_arguments", "grant_id must be a non-empty string");
            grantId = grant.GetString();
        }

        var toolArgs = args.TryGetProperty("args", out var a) ? a.Clone() : JsonElement.Parse("{}");
        if (toolArgs.ValueKind != JsonValueKind.Object)
            return Refusal("bad_arguments", "args must be an object");

        var request = new EngineDispatchRequest(context.AgentId, adapter!, grantId, studio!, tool!, toolArgs)
        {
            SessionId = context.SessionId,
            GcId = context.GcId,
        };
        var outcome = await broker.RelayAsync(request, cancellationToken).ConfigureAwait(false);
        return Shape(outcome);
    }

    internal static string Shape(EngineRelayOutcome outcome)
    {
        switch (outcome.Status)
        {
            case EngineRelayStatus.Completed when outcome.Result is { } result:
                var raw = result.Payload.GetRawText();
                if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxResultBytes)
                    return Write(w => { w.WriteString("status", "too_large"); w.WriteBoolean("ok", result.Ok); w.WriteNumber("bytes", System.Text.Encoding.UTF8.GetByteCount(raw)); });
                return Write(w => { w.WriteString("status", "completed"); w.WriteBoolean("ok", result.Ok); w.WritePropertyName("result"); result.Payload.WriteTo(w); });
            case EngineRelayStatus.Denied:
                return Write(w => { w.WriteString("status", "denied"); w.WriteString("reason", (outcome.DenyReason ?? EngineDispatchReason.ReaderFailure).ToString()); });
            case EngineRelayStatus.Timeout:
                return Write(w => { w.WriteString("status", "timeout"); w.WriteString("detail", outcome.Detail ?? "no answer"); });
            default:
                return Write(w => { w.WriteString("status", "unavailable"); w.WriteString("detail", outcome.Detail ?? "no device"); });
        }
    }

    private static string Refusal(string status, string detail) =>
        Write(w => { w.WriteString("status", status); w.WriteString("detail", detail); });

    private static bool TryString(JsonElement args, string name, out string? value)
    {
        value = null;
        if (!args.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
