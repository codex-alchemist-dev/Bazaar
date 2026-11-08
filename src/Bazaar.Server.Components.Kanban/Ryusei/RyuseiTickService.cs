using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// The RyuseiFuda scheduler tick (doc 38, Layer 2 firing, WP-R5 and WP-R6). One pass claims due timer, interval and armed watcher
/// triggers with <c>FOR UPDATE SKIP LOCKED</c>, decides each with <see cref="RyuseiTickRules"/>, and in one transaction writes the
/// fire through <see cref="RyuseiFireWriter"/>: the <c>ryusei_fire</c> row, the trigger's runtime columns (version-checked) and one
/// inbox item per woken target. The inbox item's source key is the fire id (doc 06, <c>(agent_id, source_key, kind)</c>), so a
/// retried fire is a no-op.
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
    private readonly RyuseiFireWriter _writer;
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
        _writer = new RyuseiFireWriter(dataSource);
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
        if (RyuseiTickRules.IsExpiredWatcher(trigger, now))
        {
            // Expired before its armed fire: nothing fired, so there is no fire row.
            await _writer.ExpireAsync(tx, trigger, ct).ConfigureAwait(false);
            return false;
        }

        RyuseiFireDecision? decision;
        var spec = RyuseiFireWriter.TryDecodeSpec(trigger);
        if (spec is null)
        {
            decision = RyuseiTickRules.Malformed(trigger, "The stored spec does not decode.");
        }
        else
        {
            var input = await BuildInputAsync(tx, trigger, spec, now, ct).ConfigureAwait(false);
            decision = RyuseiTickRules.Decide(input, now, _limits);
        }

        return decision is not null && await _writer.ApplyAsync(tx, trigger, decision, now, ct).ConfigureAwait(false);
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
            .Select(id => new RyuseiFireTarget(id, RyuseiFireWriter.TargetStatus(states.GetValueOrDefault(id))))
            .ToArray();
        var firesInDay = await RyuseiFireRepository.CountWokeSinceAsync(tx, trigger.TriggerId, now - TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var lastWoke = await RyuseiFireRepository.LastWokeAtAsync(tx, trigger.TriggerId, ct).ConfigureAwait(false);
        return new RyuseiTickInput(trigger, spec, _zoneOf(trigger), targets, firesInDay, lastWoke);
    }
}
