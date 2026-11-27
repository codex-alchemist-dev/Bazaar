using Bazaar.Server.OutcomeLedger.Abstractions;

namespace Bazaar.Server.OutcomeLedger;

/// <summary>The default <see cref="IOutcomeRecorder"/>.</summary>
public sealed class OutcomeRecorder(
    IOutcomeLedgerStore store,
    OutcomeWeights weights,
    TimeProvider time,
    IEnumerable<IOutcomeListener> listeners) : IOutcomeRecorder
{
    public async Task<OutcomeRecordResult> RecordAsync(OutcomeRecord record, CancellationToken cancellationToken)
    {
        var resolved = Resolve(record);
        var id = await store.AppendAsync(resolved, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (id is not { } outcomeId)
        {
            return new OutcomeRecordResult(false, null);
        }

        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnRecordedAsync(outcomeId, resolved, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Best effort: the outcome is already recorded and a listener must never undo that.
            }
        }

        return new OutcomeRecordResult(true, outcomeId);
    }

    private OutcomeRecord Resolve(OutcomeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.PrRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.DedupeKey);
        if (record.X is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(record), record.X, "Outcome x must be in [0,1].");
        }

        if (record.Attributions.Count == 0)
        {
            throw new ArgumentException("An outcome needs at least one attribution.", nameof(record));
        }

        var w = record.W ?? weights.For(record.Kind);
        if (w < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(record), w, "Outcome weight cannot be negative.");
        }

        var attributions = new List<OutcomeAttribution>(record.Attributions.Count);
        var seen = new HashSet<(string, string)>();
        foreach (var a in record.Attributions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(a.AgentId);
            ArgumentException.ThrowIfNullOrWhiteSpace(a.Role);
            ArgumentException.ThrowIfNullOrWhiteSpace(a.Tag);
            if (!seen.Add((a.AgentId, a.Tag)))
            {
                throw new ArgumentException($"Duplicate attribution for agent {a.AgentId} and tag {a.Tag}.", nameof(record));
            }

            attributions.Add(a with { W = a.W ?? w });
        }

        return record with { W = w, Attributions = attributions };
    }
}
