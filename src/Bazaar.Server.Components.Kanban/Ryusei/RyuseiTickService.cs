using System.Text.Json;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// The RyuseiFuda scheduler tick (doc 38, Layer 2 firing, WP-R5). One pass claims due timer and interval triggers with
/// <c>FOR UPDATE SKIP LOCKED</c>, decides each with <see cref="RyuseiTickRules"/>, and in one transaction writes the
/// <c>ryusei_fire</c> row, the trigger's runtime columns (version-checked) and one inbox item per woken target. The inbox
/// item's source key is the fire id (doc 06, <c>(agent_id, source_key, kind)</c>), so a retried fire is a no-op.
/// </summary>
/// <remarks>
/// The tick enqueues wakes; it never starts a turn, so the normal wake priority and digest apply (doc 38). Triggers have no
/// authority of their own, so a fire writes no task state and no GC line here (WP-R7 adds the attach and new-GC pings).
/// </remarks>
public sealed class RyuseiTickService
{
    /// <summary>The most triggers one pass claims. A later pass takes the rest.</summary>
    public const int BatchSize = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly RyuseiTriggerRepository _triggers;
    private readonly RyuseiLimits _limits;
    private readonly TimeProvider _time;
    private readonly Func<RyuseiTriggerRecord, TimeZoneInfo> _zoneOf;

    /// <param name="zoneOf">
    /// The zone that anchors an interval's wall-clock days. Null means UTC until doc 09 zones are stored (no table holds
    /// them yet).
    /// </param>
    public RyuseiTickService(
        NpgsqlDataSource dataSource,
        RyuseiLimits limits,
        TimeProvider time,
        Func<RyuseiTriggerRecord, TimeZoneInfo>? zoneOf = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        _dataSource = dataSource;
        _limits = limits;
        _time = time;
        _zoneOf = zoneOf ?? (_ => TimeZoneInfo.Utc);
        _triggers = new RyuseiTriggerRepository(dataSource);
    }

    /// <summary>Runs one pass and returns how many fire rows it wrote (woke, refused or skipped).</summary>
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var due = await _triggers.ClaimDueAsync(tx, now, BatchSize, ct).ConfigureAwait(false);
        var written = 0;
        foreach (var trigger in due)
        {
            if (await ProcessAsync(tx, trigger, now, ct).ConfigureAwait(false))
            {
                written++;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return written;
    }

    private async Task<bool> ProcessAsync(NpgsqlTransaction tx, RyuseiTriggerRecord trigger, DateTimeOffset now, CancellationToken ct)
    {
        RyuseiFireDecision? decision;
        var spec = DecodeOrNull(trigger);
        if (spec is null)
        {
            decision = RyuseiTickRules.Malformed(trigger, "The stored spec does not decode.");
        }
        else
        {
            var input = await BuildInputAsync(tx, trigger, spec, now, ct).ConfigureAwait(false);
            decision = RyuseiTickRules.Decide(input, now, _limits);
        }

        return decision is not null && await ApplyAsync(tx, trigger, decision, now, ct).ConfigureAwait(false);
    }

    private async Task<RyuseiTickInput> BuildInputAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        RyuseiTriggerSpec spec,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var states = await RyuseiFireRepository.AgentStatesAsync(tx, trigger.TargetAgentIds, ct).ConfigureAwait(false);
        var targets = trigger.TargetAgentIds
            .Select(id => new RyuseiFireTarget(id, TargetStatus(states.GetValueOrDefault(id))))
            .ToArray();
        var firesInDay = await RyuseiFireRepository.CountWokeSinceAsync(tx, trigger.TriggerId, now - TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var lastWoke = await RyuseiFireRepository.LastWokeAtAsync(tx, trigger.TriggerId, ct).ConfigureAwait(false);
        return new RyuseiTickInput(trigger, spec, _zoneOf(trigger), targets, firesInDay, lastWoke);
    }

    /// <summary>
    /// Writes one decision in the caller's transaction. Returns false, and writes nothing, when this trigger already has a fire
    /// for the instant (a crash-retried fire, doc 38). Otherwise the fire row, the trigger update and the inbox items commit together.
    /// </summary>
    private async Task<bool> ApplyAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        RyuseiFireDecision decision,
        DateTimeOffset now,
        CancellationToken ct)
    {
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

        // The row lock from the claim makes this version check always pass. It stays as the guard the rest of the service uses.
        var next = trigger with
        {
            State = decision.NextState,
            NextFireAt = decision.NextFireAt,
            FireCount = trigger.FireCount + (decision.Woke ? 1 : 0),
            ConsecutiveRefusals = decision.ConsecutiveRefusals,
        };
        if (!await _triggers.UpdateAsync(tx, next, trigger.Version, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Trigger {trigger.TriggerId:D} changed under the tick's row lock.");
        }

        if (decision.WakeReason is { } reason)
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

    private static RyuseiFireTargetStatus TargetStatus(string? agentState) => agentState switch
    {
        null => RyuseiFireTargetStatus.Missing,
        "resting" => RyuseiFireTargetStatus.Resting,
        "retired" => RyuseiFireTargetStatus.Retired,
        _ => RyuseiFireTargetStatus.Wakeable,
    };

    private static RyuseiTriggerSpec? DecodeOrNull(RyuseiTriggerRecord trigger)
    {
        try
        {
            return RyuseiSpecJson.Decode(trigger.Kind, trigger.SpecJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
