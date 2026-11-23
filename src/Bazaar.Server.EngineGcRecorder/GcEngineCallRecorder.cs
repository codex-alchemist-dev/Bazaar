using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Gc;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.EngineGcRecorder;

/// <summary>
/// Records a write or exec engine call as a system event in the group chat the agent is working in (doc 20 Audit, doc 01
/// [PROPOSED]). The event carries ids, names and the argument hash only: never arguments, results or file content. With no GC
/// named on the call it throws, and the dispatcher then denies the call, so an unrecorded call cannot run.
/// </summary>
public sealed class GcEngineCallRecorder(IGcLogWriter writer) : IEngineGcRecorder
{
    /// <summary>System author id of recorded engine calls (the same system principal the log uses for its own events).</summary>
    public static readonly Guid SystemAuthorId = Guid.Parse("00000000-0000-7000-8000-000000000001");

    /// <inheritdoc />
    public async Task RecordAsync(EngineToolCallRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.GcId is not { } gcId)
            throw new InvalidOperationException("The engine call names no group chat to record it in.");

        var request = new AppendRequest(
            gcId,
            GcEventKinds.System,
            AuthorKind.System,
            SystemAuthorId,
            SourceTrust.System,
            $"engine-call:{Guid.NewGuid():N}")
        {
            PayloadJson = Payload(record),
        };
        await writer.AppendAsync(request, ct).ConfigureAwait(false);
    }

    internal static string Payload(EngineToolCallRecord r)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("kind", "engine_call");
            json.WriteString("agent_id", r.AgentId.ToString("D"));
            json.WriteString("adapter_id", r.AdapterId);
            json.WriteString("tool", r.Tool);
            json.WriteString("risk_class", r.RiskClass.ToString().ToLowerInvariant());
            json.WriteBoolean("allowed", r.Allowed);
            json.WriteString("reason", r.Reason.ToString());
            if (r.GrantId is not null)
                json.WriteString("grant_id", r.GrantId);
            json.WriteString("args_sha256", r.ArgsSha256);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
