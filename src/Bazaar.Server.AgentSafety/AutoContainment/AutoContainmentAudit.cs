using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>Writes the audit records of automatic containment and its undo. Payloads hold ids and steps only: no alert detail, no secrets, no message content.</summary>
internal static class AutoContainmentAudit
{
    /// <summary>Audit kind of every automatic containment and undo record.</summary>
    public const string Kind = "containment.auto";

    /// <summary>The actor recorded for the containment an alert triggers.</summary>
    public static string BreachActor(string ruleId) => "anomaly:" + ruleId;

    public static Task AppendAsync(
        IAuditLog audit, TimeProvider time, string actor, string step, string agentId, string? ruleId, string? severity, DateTimeOffset? undoUntil, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["agent_id"] = agentId,
            ["step"] = step,
        };
        if (ruleId is not null)
        {
            payload["rule_id"] = ruleId;
        }
        if (severity is not null)
        {
            payload["severity"] = severity;
        }
        if (undoUntil is { } until)
        {
            payload["undo_until"] = until.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        return audit.AppendAsync(new AuditEntry(time.GetUtcNow(), actor, Kind, payload.ToJsonString()), ct);
    }
}
