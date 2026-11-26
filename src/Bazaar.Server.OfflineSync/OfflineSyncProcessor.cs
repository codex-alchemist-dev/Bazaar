using System.Collections.Concurrent;
using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync;

/// <summary>
/// Applies offline write batches: idempotent per write id, gapless per device sequence, in order,
/// serialized per device (doc 56 sections 3 and 4).
/// </summary>
public sealed class OfflineSyncProcessor
{
    private readonly Dictionary<OfflineDataClass, IOfflineMergeRule> _rules;
    private readonly IOfflineSyncStore _store;
    private readonly IOfflineMergeAudit _audit;
    private readonly IOfflineClock _clock;
    private readonly OfflineSyncOptions _options;
    private readonly IOfflineWriteScopeFactory _scopes;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _deviceLocks = new();

    /// <summary>Creates a processor. When several rules claim one data class the last one wins.</summary>
    /// <param name="rules">The merge rules available on this server.</param>
    /// <param name="store">Outcome and sequence store.</param>
    /// <param name="audit">Audit sink.</param>
    /// <param name="clock">Server clock.</param>
    /// <param name="options">Operator settings.</param>
    /// <param name="scopes">Wraps each applied write in one unit of work; null means none (in-memory state).</param>
    public OfflineSyncProcessor(
        IEnumerable<IOfflineMergeRule> rules,
        IOfflineSyncStore store,
        IOfflineMergeAudit audit,
        IOfflineClock clock,
        OfflineSyncOptions options,
        IOfflineWriteScopeFactory? scopes = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [];
        foreach (var rule in rules)
        {
            _rules[rule.DataClass] = rule;
        }

        _store = store;
        _audit = audit;
        _clock = clock;
        _options = options;
        _scopes = scopes ?? new NoOpOfflineWriteScopeFactory();
    }

    /// <summary>Processes one device batch. Concurrent calls for the same device run one after another.</summary>
    /// <param name="batch">The batch.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcomes in order, the acknowledged sequence and the gap, if any.</returns>
    /// <exception cref="ArgumentException">A write names a device other than the batch's.</exception>
    public async ValueTask<OfflineBatchResult> ProcessAsync(OfflineWriteBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var write in batch.Writes)
        {
            if (write.DeviceId != batch.DeviceId)
            {
                throw new ArgumentException("A write's DeviceId does not match the batch's DeviceId.", nameof(batch));
            }
        }

        var gate = _deviceLocks.GetOrAdd(batch.DeviceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProcessLockedAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<OfflineBatchResult> ProcessLockedAsync(OfflineWriteBatch batch, CancellationToken cancellationToken)
    {
        var deviceId = batch.DeviceId;
        var acked = await _store.GetAckedSequenceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<OfflineWriteOutcome>(batch.Writes.Count);

        foreach (var write in batch.Writes)
        {
            var stored = await _store.FindOutcomeAsync(deviceId, write.WriteId, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                outcomes.Add(stored with { Kind = OfflineOutcomeKind.Duplicate });
                continue;
            }

            var expected = acked + 1;
            if (write.DeviceSequence > expected)
            {
                return new OfflineBatchResult(outcomes, acked, expected);
            }

            OfflineWriteOutcome outcome;
            if (write.DeviceSequence < expected)
            {
                // Unseen write id on a sequence number the server already consumed: never applied, nothing to advance.
                outcome = new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.Rejected, Reason: "sequence already used");
                await _audit.RecordAsync(write, outcome, cancellationToken).ConfigureAwait(false);
                outcomes.Add(outcome);
                continue;
            }

            // The merge, the outcome record and the audit record commit together (doc 56 section 8, item 4).
            var scope = _scopes.Begin(write);
            await using (scope.ConfigureAwait(false))
            {
                outcome = await DecideAsync(write, cancellationToken).ConfigureAwait(false);
                await _store.RecordAsync(deviceId, write.DeviceSequence, outcome, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(write, outcome, cancellationToken).ConfigureAwait(false);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            acked = write.DeviceSequence;
            outcomes.Add(outcome);
        }

        return new OfflineBatchResult(outcomes, acked, null);
    }

    private async ValueTask<OfflineWriteOutcome> DecideAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        if (write.Hlc.WallMillis > _clock.NowMillis + (long)_options.MaxClockSkew.TotalMilliseconds)
        {
            return new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.HeldForReview, Reason: "clock skew beyond the allowed maximum");
        }

        if (!_rules.TryGetValue(write.DataClass, out var rule))
        {
            return new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.ModuleUnavailable, Reason: $"no module handles {write.DataClass}");
        }

        return await rule.MergeAsync(write, cancellationToken).ConfigureAwait(false);
    }
}
