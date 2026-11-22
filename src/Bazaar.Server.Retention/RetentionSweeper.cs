using System.Globalization;
using System.Text.Json.Nodes;
using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Retention;

/// <summary>Tuning for one sweep run. Both are bounds, not targets: a large backlog is worked off over several runs.</summary>
/// <param name="BatchSize">Records listed and acted on per step.</param>
/// <param name="MaxBatchesPerStep">Upper bound on batches per category and step (soft delete, purge) in one run.</param>
public sealed record RetentionSweepOptions(int BatchSize = 500, int MaxBatchesPerStep = 20);

/// <summary>What happened to one category in one run.</summary>
public enum RetentionSweepOutcome
{
    /// <summary>The category has no removal rule (no row, no period, or not soft-delete-then-purge); nothing was done.</summary>
    Kept,

    /// <summary>The sweep ran. It may have removed nothing.</summary>
    Swept,

    /// <summary>The legal hold store could not be read, so nothing was removed (fail closed).</summary>
    FailedClosed,
}

/// <summary>The result for one category. <see cref="Held"/> counts records skipped because a legal hold covers them.</summary>
public sealed record RetentionSweepResult(string Category, RetentionSweepOutcome Outcome, int SoftDeleted, int Purged, int Held);

/// <summary>
/// Runs retention over every <see cref="IRetentionStore"/> (doc 48 section 9). Rules, in order of importance:
/// legal hold wins over everything and is re-read before every destructive step, so a hold created mid-run stops the next batch;
/// if the hold store cannot be read, nothing is removed; the planner (not the store) decides what is due; crypto-erase is never
/// done here; audit rows carry counts and ids of nothing personal, and a run that did nothing writes no audit row.
/// </summary>
public sealed class RetentionSweeper(
    IEnumerable<IRetentionStore> stores,
    IRetentionPolicy policy,
    IRetentionPlanner planner,
    ILegalHoldScopeReader holds,
    IAuditLog audit,
    TimeProvider time,
    RetentionSweepOptions? options = null)
{
    /// <summary>The audit actor for every row this writes.</summary>
    public const string Actor = "retention";

    /// <summary>Audit kind: records were soft-deleted or purged.</summary>
    public const string SweptKind = "retention.swept";

    /// <summary>Audit kind: the hold store failed, so the category was skipped.</summary>
    public const string FailedClosedKind = "retention.sweep_failed_closed";

    private readonly RetentionSweepOptions _options = Validate(options ?? new RetentionSweepOptions());
    private readonly IRetentionStore[] _stores = stores.ToArray();

    /// <summary>Sweeps every store. One failing store does not stop the others.</summary>
    public async Task<IReadOnlyList<RetentionSweepResult>> SweepAsync(CancellationToken ct)
    {
        var results = new List<RetentionSweepResult>(_stores.Length);
        foreach (var store in _stores)
            results.Add(await SweepStoreAsync(store, ct).ConfigureAwait(false));
        return results;
    }

    private async Task<RetentionSweepResult> SweepStoreAsync(IRetentionStore store, CancellationToken ct)
    {
        var category = store.Category;
        var row = policy.Find(category);
        if (row is null || row.Period is null || row.Method != ErasureMethod.SoftDeleteThenPurge)
            return new RetentionSweepResult(category, RetentionSweepOutcome.Kept, 0, 0, 0);

        int softDeleted = 0, purged = 0, held = 0;
        try
        {
            // Soft delete first, then purge what is past its window (doc 48 section 9).
            for (var batch = 0; batch < _options.MaxBatchesPerStep; batch++)
            {
                var now = time.GetUtcNow();
                var records = await store.ListSoftDeleteCandidatesAsync(now - row.Period.Value, _options.BatchSize, ct).ConfigureAwait(false);
                var (ids, skipped) = await DueAsync(category, records, RetentionDisposition.SoftDeleteDue, now, ct).ConfigureAwait(false);
                held += skipped;
                if (ids.Count > 0)
                {
                    await store.SoftDeleteAsync(ids, now, ct).ConfigureAwait(false);
                    softDeleted += ids.Count;
                }

                // A short page, or a page that made no progress, ends the step: held records would otherwise be listed forever.
                if (records.Count < _options.BatchSize || ids.Count == 0)
                    break;
            }

            for (var batch = 0; batch < _options.MaxBatchesPerStep; batch++)
            {
                var now = time.GetUtcNow();
                var records = await store.ListPurgeCandidatesAsync(now - row.SoftDeleteWindow, _options.BatchSize, ct).ConfigureAwait(false);
                var (ids, skipped) = await DueAsync(category, records, RetentionDisposition.PurgeDue, now, ct).ConfigureAwait(false);
                held += skipped;
                if (ids.Count > 0)
                {
                    await store.PurgeAsync(ids, ct).ConfigureAwait(false);
                    purged += ids.Count;
                }

                if (records.Count < _options.BatchSize || ids.Count == 0)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything that goes wrong mid-sweep, a hold read included, stops this category. What was already done is reported.
            await AppendAsync(FailedClosedKind, category, ("error", ex.GetType().Name), ("softDeleted", softDeleted.ToString(CultureInfo.InvariantCulture)), ("purged", purged.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return new RetentionSweepResult(category, RetentionSweepOutcome.FailedClosed, softDeleted, purged, held);
        }

        if (softDeleted > 0 || purged > 0)
            await AppendAsync(SweptKind, category, ("softDeleted", softDeleted.ToString(CultureInfo.InvariantCulture)), ("purged", purged.ToString(CultureInfo.InvariantCulture)), ("held", held.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return new RetentionSweepResult(category, RetentionSweepOutcome.Swept, softDeleted, purged, held);
    }

    /// <summary>Re-reads the holds, then returns the ids the planner says are <paramref name="wanted"/> and no hold covers, plus how many a hold kept back.</summary>
    private async Task<(List<string> Ids, int Held)> DueAsync(string category, IReadOnlyList<RetentionRecordRef> records, RetentionDisposition wanted, DateTimeOffset now, CancellationToken ct)
    {
        var ids = new List<string>(records.Count);
        if (records.Count == 0)
            return (ids, 0);

        // Read right before the destructive call, so a hold created a moment ago still wins. A read error propagates and fails closed.
        var active = await holds.ListActiveAsync(ct).ConfigureAwait(false);
        var held = 0;
        foreach (var record in records)
        {
            var covered = LegalHoldMatcher.Covers(active, category, record);
            var disposition = planner.Decide(category, record.CreatedAt, record.SoftDeletedAt, now, covered);
            if (disposition == RetentionDisposition.Held)
                held++;
            else if (disposition == wanted)
                ids.Add(record.Id);
        }

        return (ids, held);
    }

    private Task AppendAsync(string kind, string category, params (string Key, string Value)[] extra)
    {
        var payload = new JsonObject { ["category"] = category };
        foreach (var (key, value) in extra)
            payload[key] = value;
        return audit.AppendAsync(new AuditEntry(time.GetUtcNow(), Actor, kind, payload.ToJsonString()));
    }

    private static RetentionSweepOptions Validate(RetentionSweepOptions o)
    {
        if (o.BatchSize < 1 || o.MaxBatchesPerStep < 1)
            throw new ArgumentOutOfRangeException(nameof(o), "BatchSize and MaxBatchesPerStep must be at least one.");
        return o;
    }
}
