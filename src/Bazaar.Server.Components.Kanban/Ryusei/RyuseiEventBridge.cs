using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>What one event did to the triggers it matched.</summary>
/// <param name="Fired">Event triggers that woke or skipped their targets (one fire row each).</param>
/// <param name="Armed">Watchers whose next fire time was set by this event.</param>
/// <param name="Expired">Triggers that expired by time, or whose targets are all gone.</param>
public sealed record RyuseiBridgeResult(int Fired, int Armed, int Expired);

/// <summary>
/// The RyuseiFuda event bridge (doc 38, WP-R6). For one event it locks the active event and watcher triggers that could match
/// (in one project, or every project for a GC-sourced event), applies the predicate, and writes the result in one transaction.
/// An event trigger fires on its first match through <see cref="RyuseiFireWriter"/>. A watcher is armed with its debounce
/// deadline, and the scheduler tick fires it when that deadline passes.
/// </summary>
/// <remarks>
/// The bridge takes an already-mapped <see cref="RyuseiEventEnvelope"/>. Reading the source rows and mapping them is the
/// listener's job (<see cref="RyuseiEventListener"/>), which keeps this class free of channel details.
/// </remarks>
public sealed class RyuseiEventBridge
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RyuseiTriggerRepository _triggers;
    private readonly RyuseiFireWriter _writer;

    /// <param name="audit">The fire audit log (doc 38). Null skips the fire audit line only; the fire rows are still written.</param>
    public RyuseiEventBridge(NpgsqlDataSource dataSource, ITransactionalAuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _triggers = new RyuseiTriggerRepository(dataSource);
        _writer = new RyuseiFireWriter(dataSource, audit);
    }

    /// <summary>Applies one event to every matching active event and watcher trigger. Commits once.</summary>
    public async Task<RyuseiBridgeResult> HandleAsync(RyuseiEventEnvelope evt, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var fired = 0;
        var armed = 0;
        var expired = 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var candidates = await _triggers.LockEventCandidatesAsync(tx, evt.ProjectId, ct).ConfigureAwait(false);
        foreach (var trigger in candidates)
        {
            var spec = RyuseiFireWriter.TryDecodeSpec(trigger);
            var predicate = spec switch
            {
                RyuseiEventTriggerSpec e => e.Predicate,
                RyuseiWatcherTriggerSpec w => w.Predicate,
                _ => null,
            };

            // A spec that does not decode is skipped here. The tick pauses malformed triggers, so an event never repairs one.
            if (predicate is null
                || !RyuseiEventRules.IsInScope(predicate, evt)
                || !RyuseiPredicateMatcher.Matches(predicate, evt.ToMatchEvent()))
            {
                continue;
            }

            if (spec is RyuseiEventTriggerSpec)
            {
                var decision = await DecideEventAsync(tx, trigger, evt, now, ct).ConfigureAwait(false);
                if (decision.Fire is { } fire)
                {
                    await _writer.ApplyAsync(tx, trigger, fire, now, ct: ct).ConfigureAwait(false);
                    fired++;
                }
                else
                {
                    await UpdateOrThrowAsync(tx, trigger with { State = decision.NextState, NextFireAt = null }, trigger.Version, ct).ConfigureAwait(false);
                    expired++;
                }
            }
            else if (spec is RyuseiWatcherTriggerSpec watcher)
            {
                var match = RyuseiEventRules.MatchWatcher(trigger, watcher, now);
                if (!match.Changed)
                {
                    continue;
                }

                await UpdateOrThrowAsync(
                    tx,
                    trigger with { State = match.NextState, NextFireAt = match.NextFireAt },
                    trigger.Version,
                    ct).ConfigureAwait(false);
                if (match.NextState == RyuseiTriggerState.Expired)
                {
                    expired++;
                }
                else
                {
                    armed++;
                }
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RyuseiBridgeResult(fired, armed, expired);
    }

    private static async Task<RyuseiEventDecision> DecideEventAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        RyuseiEventEnvelope evt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var states = await RyuseiFireRepository.AgentStatesAsync(tx, trigger.TargetAgentIds, ct).ConfigureAwait(false);
        var targets = trigger.TargetAgentIds
            .Select(id => new RyuseiFireTarget(id, RyuseiFireWriter.TargetStatus(states.GetValueOrDefault(id))))
            .ToArray();
        return RyuseiEventRules.DecideEventMatch(trigger, targets, now, evt.Kind);
    }

    private async Task UpdateOrThrowAsync(NpgsqlTransaction tx, RyuseiTriggerRecord next, long expectedVersion, CancellationToken ct)
    {
        // The candidate rows are locked by this transaction, so the version check passes. A failure means the lock was lost.
        if (!await _triggers.UpdateAsync(tx, next, expectedVersion, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Trigger {next.TriggerId:D} changed under the row lock.");
        }
    }
}
