using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>The GC line an attach ping posted, as the inbox items need it: the GC and the line's seq (doc 38, WP-R7).</summary>
public sealed record RyuseiPingPosted(Guid GcId, long Seq);

/// <summary>
/// Builds the system line an attach-mode fire posts in its GC (doc 38, Ping, attach and new-GC semantics). The line carries ids
/// only, never the trigger's purpose text, because purpose is agent-written and the line is read by every member. The client
/// message id is keyed on the trigger and the scheduled instant, so a retried fire posts the same line and the GC returns the
/// original seq instead of a second line.
/// </summary>
public static class RyuseiPingLine
{
    /// <summary>The <c>kind</c> field in the line's payload.</summary>
    public const string PayloadKind = "ryusei.ping";

    /// <summary>Builds the append for one fire. The trigger must be an attach-mode trigger with a GC.</summary>
    public static AppendRequest Build(RyuseiTriggerRecord trigger, DateTimeOffset scheduledFor, IReadOnlyList<Guid> wakeAgentIds)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(wakeAgentIds);
        if (trigger.PingMode != RyuseiPingMode.Attach || trigger.AttachGcId is not { } gcId)
        {
            throw new ArgumentException("Only an attach-mode trigger with a GC posts a ping line.", nameof(trigger));
        }

        var fireAt = scheduledFor.ToUniversalTime();
        var targets = new JsonArray();
        foreach (var agentId in wakeAgentIds)
        {
            targets.Add(agentId.ToString("D"));
        }

        var payload = new JsonObject
        {
            ["kind"] = PayloadKind,
            ["trigger_id"] = trigger.TriggerId.ToString("D"),
            ["fire_at"] = fireAt.ToString("O"),
            ["targets"] = targets,
        };

        // The system actor is the nil id, as TaskService uses for its system lines.
        return new AppendRequest(
            gcId,
            GcEventKinds.System,
            AuthorKind.System,
            Guid.Empty,
            SourceTrust.System,
            $"ryusei:{trigger.TriggerId:D}:{fireAt:O}")
        {
            PayloadJson = payload.ToJsonString(),
        };
    }
}
