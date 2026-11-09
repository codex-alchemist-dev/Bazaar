using System.Text.Json;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// Writes one fire decision (doc 38, Firing) in the caller's transaction: the <c>ryusei_fire</c> row, the trigger's runtime
/// columns (version-checked) and one inbox item per woken target. The inbox item's source key is the fire id (doc 06), so a
/// retried fire is a no-op. The scheduler tick (WP-R5) and the event bridge (WP-R6) both write through here, so they follow
/// the same fire rules.
/// </summary>
public sealed class RyuseiFireWriter
{
    private readonly RyuseiTriggerRepository _triggers;

    public RyuseiFireWriter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _triggers = new RyuseiTriggerRepository(dataSource);
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
    /// attach fire, WP-R7), each target's wake is keyed by the GC line's (gc_id, gc_seq) instead of the fire id.
    /// </summary>
    public async Task<bool> ApplyAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        RyuseiFireDecision decision,
        DateTimeOffset now,
        RyuseiPingPosted? ping = null,
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

        if (ping is not null)
        {
            var priority = checked((short)InboxPriority.Rank(InboxKind.RyuseiPing));
            foreach (var agentId in decision.WakeAgentIds)
            {
                await InboxRepository.EnqueueAsync(
                    tx,
                    new InboxEnqueue(agentId, InboxKind.RyuseiPing, priority, GcId: ping.GcId, GcSeq: ping.Seq),
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

        return true;
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
