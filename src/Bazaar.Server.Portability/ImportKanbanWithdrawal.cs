namespace Bazaar.Server.Portability;

/// <summary>How a withdrawn Kanban task is handled (doc 44 section 8.8, following the doc 12 gates).</summary>
public enum KanbanWithdrawalMode
{
    /// <summary>Transition to Blocked with reason <see cref="ImportKanbanWithdrawer.Reason"/>.</summary>
    Block,

    /// <summary>Archive the task.</summary>
    Archive,
}

/// <summary>
/// Kanban side of a withdrawal. The host applies the transition through the normal Kanban gates, so a gate that refuses
/// makes the call throw and nothing is recorded for that batch.
/// </summary>
public interface IKanbanWithdrawalWriter
{
    /// <summary>Ids of tasks this job created, and the ones already withdrawn.</summary>
    Task<IReadOnlyList<string>> ReadImportedTaskIdsAsync(string jobId, CancellationToken ct);

    Task<IReadOnlySet<string>> ReadWithdrawnTaskIdsAsync(string jobId, CancellationToken ct);

    Task WithdrawTasksAsync(string jobId, int batchIndex, IReadOnlyList<string> taskIds, KanbanWithdrawalMode mode, string reason, CancellationToken ct);
}

/// <summary>Withdraws the Kanban tasks of an import in audited batches.</summary>
public static class ImportKanbanWithdrawer
{
    /// <summary>The transition reason doc 44 section 8.8 names.</summary>
    public const string Reason = "withdrawn by import";

    public static async Task<ImportWithdrawalResult> WithdrawAsync(
        string jobId,
        IKanbanWithdrawalWriter tasks,
        IAuditSink audit,
        KanbanWithdrawalMode mode,
        int batchSize,
        TimeProvider now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var imported = await tasks.ReadImportedTaskIdsAsync(jobId, ct);
        var done = await tasks.ReadWithdrawnTaskIdsAsync(jobId, ct);
        var pending = imported.Where(id => !done.Contains(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var skipped = imported.Count(done.Contains);

        var withdrawn = new List<string>();
        var batches = 0;
        var index = 0;
        foreach (var chunk in pending.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await tasks.WithdrawTasksAsync(jobId, index, chunk, mode, Reason, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ImportWithdrawalResult(batches, withdrawn, skipped, new ImportBatchFailure(index, ImportTargetKind.VaultFile, ImportBatchStage.Write, ex.Message));
            }

            batches++;
            withdrawn.AddRange(chunk);
            try
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', chunk)))).ToLowerInvariant();
                await audit.AppendAsync(new ImportAuditRecord(ImportWithdrawer.AuditKind, jobId, index + 1, chunk.Length, hash, now.GetUtcNow()), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ImportWithdrawalResult(batches, withdrawn, skipped, new ImportBatchFailure(index, ImportTargetKind.VaultFile, ImportBatchStage.Audit, ex.Message));
            }
            index++;
        }
        return new ImportWithdrawalResult(batches, withdrawn, skipped, null);
    }
}
