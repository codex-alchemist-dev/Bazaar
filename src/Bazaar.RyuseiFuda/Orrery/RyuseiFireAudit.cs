using System.Text.Json.Nodes;
using Bazaar.Server.Kanban;
using Bazaar.Server.Security.Audit;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// The audit row for one RyuseiFuda fire (doc 38, Authority, audit and taint; doc 23). The fire table is the operational record and
/// this row is the security record. Pure: it builds the entry and reads no clock or database.
/// </summary>
/// <remarks>
/// A woke fire is <c>ryusei.trigger.fired</c>. Every other decided fire (a refused cap or cooldown, a skipped resting target set,
/// or an error) is <c>ryusei.trigger.fire_refused</c>, with its outcome in the payload. The payload carries ids, the outcome and
/// the cause only. It never carries the trigger's purpose text.
/// </remarks>
public static class RyuseiFireAudit
{
    /// <summary>Audit kind for a fire that woke its targets.</summary>
    public const string FiredKind = "ryusei.trigger.fired";

    /// <summary>Audit kind for a fire that did not wake anyone: refused, skipped or in error.</summary>
    public const string FireRefusedKind = "ryusei.trigger.fire_refused";

    /// <summary>Audit kind for a trigger that expired without a fire row: a watcher past its deadline, or an event trigger past its
    /// expiry or with no target left. Nothing fired, but the state change is still a security record.</summary>
    public const string ExpiredKind = "ryusei.trigger.expired";

    /// <summary>
    /// The actor of a fire. A fire is written by the scheduler tick or the event bridge, never by a person, so it is the system
    /// actor (the same text as <c>TaskActor.SystemActor</c>). The trigger's creator is recorded in the payload as <c>createdBy</c>.
    /// </summary>
    public const string SystemActor = "system";

    /// <summary>The audit kind for a decided fire outcome.</summary>
    public static string KindFor(RyuseiFireOutcome outcome) =>
        outcome == RyuseiFireOutcome.Woke ? FiredKind : FireRefusedKind;

    /// <summary>
    /// Builds the audit entry for one fire. <paramref name="fireId"/> is the <c>ryusei_fire</c> row id, so the security record points
    /// at the operational one.
    /// </summary>
    public static AuditEntry Build(Guid fireId, RyuseiTriggerRecord trigger, RyuseiFireDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(decision);

        var wokenIds = new JsonArray();
        foreach (var agentId in decision.WakeAgentIds)
        {
            wokenIds.Add((JsonNode)agentId.ToString("D"));
        }

        var payload = new JsonObject
        {
            ["fireId"] = fireId.ToString("D"),
            ["triggerId"] = trigger.TriggerId.ToString("D"),
            ["projectId"] = trigger.ProjectId.ToString("D"),
            ["createdBy"] = trigger.CreatedBy.ToString("D"),
            ["outcome"] = RyuseiTickRules.ToSql(decision.Outcome),
            ["scheduledFor"] = decision.ScheduledFor.ToUniversalTime().ToString("O"),
            ["wokenAgents"] = wokenIds,
            ["cause"] = decision.Cause.DeepClone(),
        };

        return new AuditEntry(now, SystemActor, KindFor(decision.Outcome), payload.ToJsonString());
    }

    /// <summary>
    /// Builds the audit entry for a trigger that expired without a fire. <paramref name="reason"/> is a short machine label such as
    /// <c>watcher_past_expiry</c>. The payload carries ids and the reason only, never the purpose text.
    /// </summary>
    public static AuditEntry BuildExpiry(RyuseiTriggerRecord trigger, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        var payload = new JsonObject
        {
            ["triggerId"] = trigger.TriggerId.ToString("D"),
            ["projectId"] = trigger.ProjectId.ToString("D"),
            ["createdBy"] = trigger.CreatedBy.ToString("D"),
            ["reason"] = reason,
        };

        return new AuditEntry(now, SystemActor, ExpiredKind, payload.ToJsonString());
    }
}
