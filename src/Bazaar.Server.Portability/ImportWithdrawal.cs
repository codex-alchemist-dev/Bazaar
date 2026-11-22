using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// Write side of a withdrawal (doc 44 section 8.8). Nothing is deleted. A GC entry gets a new version marked
/// <c>withdrawn_by_import: &lt;job&gt;</c>, and a Vault file is restored to its prior path state by a new commit.
/// Each call handles one batch and succeeds or fails as a whole.
/// </summary>
public interface IImportWithdrawalWriter
{
    /// <summary>Source keys of this job already withdrawn, GC and Vault. A re-run skips them.</summary>
    Task<IReadOnlySet<string>> ReadWithdrawnSourceKeysAsync(string jobId, CancellationToken ct);

    /// <summary>Adds the withdrawn version to one batch of GC entries. Throws if the batch cannot be written.</summary>
    Task WithdrawGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct);

    /// <summary>Reverts one batch of Vault files with a new commit. Throws if the batch cannot be written.</summary>
    Task RevertVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct);
}

/// <summary>What a withdrawal would touch, for the owner to confirm first.</summary>
public sealed record ImportWithdrawalPlan(string JobId, int GcCount, int VaultCount, int AlreadyWithdrawn)
{
    public int Total => GcCount + VaultCount;
}

/// <summary>
/// Outcome of a withdrawal. <see cref="BatchCount"/> is the number of batches whose write succeeded.
/// <see cref="WithdrawnSourceKeys"/> lists the keys withdrawn by this run.
/// </summary>
public sealed record ImportWithdrawalResult(
    int BatchCount,
    IReadOnlyList<string> WithdrawnSourceKeys,
    int SkippedCount,
    ImportBatchFailure? FirstFailure)
{
    public bool Succeeded => FirstFailure is null;
}

/// <summary>Withdraws a committed import job in batches, auditing each batch (doc 44 section 8.8).</summary>
public static class ImportWithdrawer
{
    /// <summary>The audit kind written for each withdrawn batch.</summary>
    public const string AuditKind = "portability.import.withdraw";

    /// <summary>Counts what a withdrawal of the job would touch. Read-only.</summary>
    public static async Task<ImportWithdrawalPlan> PlanAsync(
        string jobId,
        IImportTargetWriter target,
        IImportWithdrawalWriter withdrawals,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(withdrawals);

        var done = await withdrawals.ReadWithdrawnSourceKeysAsync(jobId, ct);
        var gc = Pending(await target.ReadGcSourceKeysAsync(jobId, ct), done);
        var vault = Pending(await target.ReadVaultSourceKeysAsync(jobId, ct), done);
        var already = (await target.ReadGcSourceKeysAsync(jobId, ct)).Concat(await target.ReadVaultSourceKeysAsync(jobId, ct)).Count(done.Contains);
        return new ImportWithdrawalPlan(jobId, gc.Count, vault.Count, already);
    }

    /// <summary>
    /// Withdraws every not-yet-withdrawn record of the job: GC entries first, then Vault files, in batches of at most
    /// <paramref name="batchSize"/>. Stops at the first failure. A re-run continues where the last one stopped.
    /// </summary>
    public static async Task<ImportWithdrawalResult> WithdrawAsync(
        string jobId,
        IImportTargetWriter target,
        IImportWithdrawalWriter withdrawals,
        IAuditSink audit,
        int batchSize,
        TimeProvider now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(withdrawals);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var done = await withdrawals.ReadWithdrawnSourceKeysAsync(jobId, ct);
        var gcKeys = Pending(await target.ReadGcSourceKeysAsync(jobId, ct), done);
        var vaultKeys = Pending(await target.ReadVaultSourceKeysAsync(jobId, ct), done);
        var skipped = (await target.ReadGcSourceKeysAsync(jobId, ct)).Count(done.Contains)
            + (await target.ReadVaultSourceKeysAsync(jobId, ct)).Count(done.Contains);

        var withdrawn = new List<string>();
        var batches = 0;
        var index = 0;
        foreach (var (kind, keys) in new[] { (ImportTargetKind.GcMessage, gcKeys), (ImportTargetKind.VaultFile, vaultKeys) })
        {
            foreach (var chunk in keys.Chunk(batchSize))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (kind == ImportTargetKind.GcMessage)
                    {
                        await withdrawals.WithdrawGcMessagesAsync(jobId, index, chunk, ct);
                    }
                    else
                    {
                        await withdrawals.RevertVaultFilesAsync(jobId, index, chunk, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ImportWithdrawalResult(batches, withdrawn, skipped, new ImportBatchFailure(index, kind, ImportBatchStage.Write, ex.Message));
                }

                batches++;
                withdrawn.AddRange(chunk);
                try
                {
                    await audit.AppendAsync(
                        new ImportAuditRecord(AuditKind, jobId, index + 1, chunk.Length, HashKeys(chunk), now.GetUtcNow()),
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ImportWithdrawalResult(batches, withdrawn, skipped, new ImportBatchFailure(index, kind, ImportBatchStage.Audit, ex.Message));
                }
                index++;
            }
        }

        return new ImportWithdrawalResult(batches, withdrawn, skipped, null);
    }

    private static List<string> Pending(IReadOnlyList<string> keys, IReadOnlySet<string> done) =>
        keys.Where(k => !done.Contains(k)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static string HashKeys(IReadOnlyList<string> keys) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys)))).ToLowerInvariant();
}
