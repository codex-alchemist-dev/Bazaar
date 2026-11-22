using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>
/// In-memory <see cref="IImportJobStore"/> with the same rules as the Postgres store: the same input checks, the same
/// transition rules, and a batch that is checked in full before anything is written. Used to pin the interface behaviour
/// without a database.
/// </summary>
public sealed class InMemoryImportJobStore : IImportJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JobRow> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string JobId, int Number), ImportBatchRecord> _batches = new();
    private readonly List<(ImportItemRecord Item, DateTimeOffset RecordedAt)> _items = [];

    public Task CreateJobAsync(NewImportJob job, CancellationToken ct)
    {
        ImportStoreRules.ValidateNewJob(job);
        lock (_gate)
        {
            if (_jobs.ContainsKey(job.JobId))
            {
                throw new ImportStoreConflictException("Import job " + job.JobId + " already exists.");
            }
            _jobs[job.JobId] = new JobRow(job);
        }
        return Task.CompletedTask;
    }

    public Task<ImportJobRecord?> GetJobAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        lock (_gate)
        {
            return Task.FromResult(_jobs.TryGetValue(jobId, out var row) ? row.ToRecord() : null);
        }
    }

    public Task<bool> RecordDryRunAsync(string jobId, string mappingHash, string dryRunHash, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        if (!ImportStoreRules.IsSha256Hex(mappingHash) || !ImportStoreRules.IsSha256Hex(dryRunHash))
        {
            throw new ArgumentException("Mapping and dry-run hashes must be SHA-256 digests in lowercase hex.");
        }
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var row) || row.State is not (ImportJobState.Staged or ImportJobState.DryRun))
            {
                return Task.FromResult(false);
            }
            row.MappingHash = mappingHash;
            row.DryRunHash = dryRunHash;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TransitionAsync(string jobId, ImportJobState target, DateTimeOffset at, string? reason, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var row) || !ImportJobStates.CanTransition(row.State, target))
            {
                return Task.FromResult(false);
            }
            row.State = target;
            if (target == ImportJobState.Committing)
            {
                row.StartedAt ??= at;
            }
            if (ImportJobStates.IsTerminal(target))
            {
                row.EndedAt = at;
            }
            row.FailureReason = reason ?? row.FailureReason;
            return Task.FromResult(true);
        }
    }

    public Task RecordBatchAsync(ImportBatchRecord batch, IReadOnlyList<ImportItemRecord> items, CancellationToken ct)
    {
        ImportStoreRules.ValidateBatch(batch, items);
        lock (_gate)
        {
            if (!_jobs.TryGetValue(batch.JobId, out var row))
            {
                throw new InvalidOperationException("No import job " + batch.JobId + ".");
            }
            if (row.State != ImportJobState.Committing)
            {
                throw new InvalidOperationException("Import job " + batch.JobId + " is " + row.State + ", not committing; the batch is refused.");
            }
            // Every check runs before the first write, as the transaction does in Postgres.
            if (_batches.ContainsKey((batch.JobId, batch.BatchNumber)))
            {
                throw new ImportStoreConflictException("Batch " + batch.BatchNumber + " of job " + batch.JobId + " is already recorded.");
            }
            foreach (var item in items)
            {
                if (_items.Any(e => SameIdentity(e.Item, item)))
                {
                    throw new ImportStoreConflictException("Item '" + item.SourceKey + "' is already recorded for this version.");
                }
            }

            _batches[(batch.JobId, batch.BatchNumber)] = batch;
            foreach (var item in items)
            {
                _items.Add((item, batch.CommittedAt));
            }
            row.LastProgressAt = batch.CommittedAt;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ImportBatchRecord>> ListBatchesAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        lock (_gate)
        {
            IReadOnlyList<ImportBatchRecord> batches = [.. _batches.Values.Where(b => b.JobId == jobId).OrderBy(b => b.BatchNumber)];
            return Task.FromResult(batches);
        }
    }

    public Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        lock (_gate)
        {
            IReadOnlySet<string> keys = _items.Where(e => e.Item.JobId == jobId).Select(e => e.Item.SourceKey).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(keys);
        }
    }

    public Task<ImportItemRecord?> GetLatestItemAsync(string importerId, string sourceKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        lock (_gate)
        {
            var latest = _items
                .Where(e => e.Item.ImporterId == importerId && e.Item.SourceKey == sourceKey)
                .OrderByDescending(e => e.RecordedAt)
                .Select(e => (ImportItemRecord?)e.Item)
                .FirstOrDefault();
            return Task.FromResult(latest);
        }
    }

    private static bool SameIdentity(ImportItemRecord a, ImportItemRecord b) =>
        a.ImporterId == b.ImporterId && a.SourceKey == b.SourceKey && a.TargetVersion == b.TargetVersion;

    /// <summary>Mutable job row, kept behind the lock.</summary>
    private sealed class JobRow(NewImportJob job)
    {
        public ImportJobState State { get; set; } = ImportJobState.Staged;
        public string? MappingHash { get; set; }
        public string? DryRunHash { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? LastProgressAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string? FailureReason { get; set; }

        public ImportJobRecord ToRecord() => new(
            job.JobId,
            job.ActorId,
            job.ImporterId,
            job.ImporterVersion,
            State,
            job.MaxUploadBytes,
            job.SourceManifest,
            MappingHash,
            DryRunHash,
            job.CreatedAt,
            StartedAt,
            LastProgressAt,
            EndedAt,
            FailureReason);
    }
}
