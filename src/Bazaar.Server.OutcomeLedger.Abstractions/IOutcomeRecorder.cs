namespace Bazaar.Server.OutcomeLedger.Abstractions;

/// <summary>Records outcome evidence in the append-only ledger (doc 11, work package 3).</summary>
public interface IOutcomeRecorder
{
    /// <summary>
    /// Validates the record, fills in default weights, appends it once (idempotent on <c>DedupeKey</c>), then tells the
    /// listeners. Listeners are told only when the row is new. A failing listener never fails the recording.
    /// </summary>
    Task<OutcomeRecordResult> RecordAsync(OutcomeRecord record, CancellationToken cancellationToken);
}

/// <summary>Append-only storage for outcomes. Returns the new id, or null when the dedupe key already exists.</summary>
public interface IOutcomeLedgerStore
{
    /// <summary>Appends the outcome and its attributions in one step. Every weight is already resolved.</summary>
    Task<Guid?> AppendAsync(OutcomeRecord resolved, DateTimeOffset recordedAt, CancellationToken cancellationToken);
}

/// <summary>Told after a new outcome is recorded, for example the Psych Ward watcher or a score projection.</summary>
public interface IOutcomeListener
{
    /// <summary>Called once per newly recorded outcome. Exceptions are swallowed by the recorder.</summary>
    Task OnRecordedAsync(Guid outcomeId, OutcomeRecord resolved, CancellationToken cancellationToken);
}
