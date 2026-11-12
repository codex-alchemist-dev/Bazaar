using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// Writes one fire decision (doc 38, Firing) in the caller's transaction: the <c>ryusei_fire</c> row, the trigger's runtime
/// columns (version-checked) and one inbox item per woken target. The inbox item's source key is the fire id (doc 06), so a
/// retried fire is a no-op. The scheduler tick (WP-R5) and the event bridge (WP-R6) both write through here, so they follow
/// the same fire rules.
/// </summary>
public sealed class RyuseiFireWriter
{
    private readonly RyuseiTriggerRepository _triggers;
    private readonly ITransactionalAuditLog? _audit;

    /// <param name="audit">
    /// The audit log for fire rows (doc 38). Null means no audit log is installed: the fire is still written, and only its audit line
    /// is skipped. Production always passes one, so this is for the bare constructors and tests.
    /// </param>
    public RyuseiFireWriter(NpgsqlDataSource dataSource, ITransactionalAuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _triggers = new RyuseiTriggerRepository(dataSource);
        _audit = audit;
    }

    /// <summary>Maps an agent's <c>agent.state</c> to what a fire sees. An id with no row is missing.</summary>
    public static RyuseiFireTargetStatus TargetStatus(string? agentState) => agentState switch
    {
        null => RyuseiFireTargetStatus.Missing,
        "resting" => RyuseiFireTargetStatus.Resting,
        "retired" => RyuseiFireTargetStatus.Retired,
        _ => RyuseiFireTargetStatus.Wakeable,
    };

    /// <summary>Decodes a stored spec, or returns null when it does not decode. The caller decides what a bad spec means.</summary>
    public static RyuseiTriggerSpec? TryDecodeSpec(RyuseiTriggerRecord trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        try
        {
            return RyuseiSpecJson.Decode(trigger.Kind, trigger.SpecJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes one decision. Returns false, and writes nothing, when this trigger already has a fire for the instant (a crash-retried
    /// fire, doc 38). Otherwise the fire row, the trigger update and the inbox items commit together with the caller's transaction.
    /// The caller must hold the trigger's row lock, which makes the version check pass. When <paramref name="ping"/> is set (an
    /// attach fire, WP-R7), each target's wake is keyed by the GC line's (gc_id, gc_seq) instead of the fire id. The fire's audit row
    /// (<see cref="RyuseiFireAudit"/>) is appended in the same transaction, so a crash cannot leave a fire without its audit row.
    /// When <paramref name="observed"/> is set, the fire's Observatory events (<see cref="RyuseiObservatoryEvents"/>) are added to it.
    /// The caller emits them after its transaction commits, so an event never describes a fire that rolled back.
    /// </summary>
    public async Task<bool> ApplyAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        RyuseiFireDecision decision,
        DateTimeOffset now,
        RyuseiPingPosted? ping = null,
        List<RyuseiObservatoryEvent>? observed = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(decision);

        var fireId = Guid.CreateVersion7();
        var inserted = await RyuseiFireRepository.InsertAsync(
            tx,
            fireId,
            trigger.TriggerId,
            decision.ScheduledFor,
            RyuseiTickRules.ToSql(decision.Outcome),
            decision.Cause,
            now,
            ct).ConfigureAwait(false);
        if (!inserted)
        {
            return false;
        }

        var next = trigger with
        {
            State = decision.NextState,
            NextFireAt = decision.NextFireAt,
            FireCount = trigger.FireCount + (decision.Woke ? 1 : 0),
            ConsecutiveRefusals = decision.ConsecutiveRefusals,
        };
        if (!await _triggers.UpdateAsync(tx, next, trigger.Version, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Trigger {trigger.TriggerId:D} changed under the row lock.");
        }

        if (decision.Trip is { } trip)
        {
            // A guard trip is recorded in the same transaction as the pause, so a crash cannot leave a paused trigger with no trip row.
            var counters = new JsonObject
            {
                ["triggerId"] = trigger.TriggerId.ToString("D"),
                ["fireId"] = fireId.ToString("D"),
                ["refusal"] = trip.ToString(),
                ["consecutiveRefusals"] = decision.ConsecutiveRefusals,
                ["agentIds"] = AgentIdArray(trigger.TargetAgentIds),
                ["scheduledFor"] = decision.ScheduledFor.ToString("O", CultureInfo.InvariantCulture),
            };
            await RyuseiFireRepository.InsertGuardEventAsync(tx, RyuseiTickRules.GuardEventKind(trip), counters, now, ct).ConfigureAwait(false);
        }

        if (_audit is not null)
        {
            var entry = RyuseiFireAudit.Build(fireId, trigger, decision, now);
            await _audit.AppendInTransactionAsync(
                tx.Connection ?? throw new InvalidOperationException("The fire transaction has no connection."),
                tx,
                entry,
                ct).ConfigureAwait(false);
        }

        if (ping is not null)
        {
            var priority = checked((short)InboxPriority.Rank(InboxKind.RyuseiPing));
            foreach (var agentId in decision.WakeAgentIds)
            {
                await InboxRepository.EnqueueAsync(
                    tx,
                    // SourceRef lets the woken turn find its fire. The ref dedupe index covers only rows with no GC id, so it
                    // does not apply to this row; the GC dedupe key (agent, gc, seq, kind) still makes the ping idempotent.
                    new InboxEnqueue(agentId, InboxKind.RyuseiPing, priority, GcId: ping.GcId, GcSeq: ping.Seq, SourceRef: fireId.ToString("D")),
                    ct).ConfigureAwait(false);
            }
        }
        else if (decision.WakeReason is { } reason)
        {
            var priority = checked((short)InboxPriority.Rank(reason));
            foreach (var agentId in decision.WakeAgentIds)
            {
                await InboxRepository.EnqueueAsync(
                    tx,
                    new InboxEnqueue(agentId, reason, priority, SourceRef: fireId.ToString("D")),
                    ct).ConfigureAwait(false);
            }
        }

        observed?.AddRange(RyuseiObservatoryEvents.ForFire(fireId, trigger, decision, now));
        return true;
    }

    private static JsonArray AgentIdArray(IEnumerable<Guid> agentIds)
    {
        var array = new JsonArray();
        foreach (var agentId in agentIds)
        {
            array.Add(agentId.ToString("D"));
        }

        return array;
    }

    /// <summary>Moves a trigger to <c>expired</c> without a fire row, for a watcher past its <c>expiresAt</c>.</summary>
    public async Task ExpireAsync(NpgsqlTransaction tx, RyuseiTriggerRecord trigger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(trigger);
        var next = trigger with { State = RyuseiTriggerState.Expired, NextFireAt = null };
        if (!await _triggers.UpdateAsync(tx, next, trigger.Version, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Trigger {trigger.TriggerId:D} changed under the row lock.");
        }
    }
}
