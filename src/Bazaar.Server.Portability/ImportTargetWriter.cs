namespace Bazaar.Server.Portability;

/// <summary>
/// The real target writer: GC entries through <see cref="GcImportTarget"/>, Vault files through <see cref="VaultImportTarget"/>.
/// </summary>
public sealed class ImportTargetWriter(GcImportTarget gc, VaultImportTarget vault, IKanbanImportTarget? kanban = null) : IImportTargetWriter
{
    public async Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        keys.UnionWith(await gc.ReadSourceKeysAsync(jobId, ct));
        keys.UnionWith(await vault.ReadSourceKeysAsync(jobId, ct));
        keys.UnionWith(await ReadKanbanSourceKeysAsync(jobId, ct));
        return keys;
    }

    public Task AppendKanbanTasksAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) =>
        kanban is null
            ? throw new NotSupportedException("No Kanban import target is wired into this writer.")
            : kanban.AppendBatchAsync(jobId, batchIndex, items, ct);

    public Task<IReadOnlyList<string>> ReadKanbanSourceKeysAsync(string jobId, CancellationToken ct) =>
        kanban is null
            ? Task.FromResult<IReadOnlyList<string>>([])
            : kanban.ReadSourceKeysAsync(jobId, ct);

    public Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) =>
        gc.AppendBatchAsync(jobId, batchIndex, items, ct);

    public Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) =>
        vault.WriteBatchAsync(jobId, batchIndex, items, ct);

    public Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct) =>
        gc.ReadSourceKeysAsync(jobId, ct);

    public Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct) =>
        vault.ReadSourceKeysAsync(jobId, ct);
}

/// <summary>
/// Verify stage (doc 44 section 8.1, stage 10). After a commit, it re-reads both targets through the same writer and
/// compares what it finds with what the commit reported as written. It returns a list of problems; an empty list means
/// the job verified.
/// </summary>
public static class ImportVerifier
{
    public static async Task<IReadOnlyList<string>> VerifyAsync(
        IImportTargetWriter writer,
        ImportPipelineResult dryRun,
        ImportCommitResult commit,
        string jobId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(dryRun);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var problems = new List<string>();
        var newRecords = dryRun.Preview?.New ?? [];
        var committed = new HashSet<string>(commit.CommittedSourceKeys, StringComparer.Ordinal);
        var expectedGc = new HashSet<string>(StringComparer.Ordinal);
        var expectedVault = new HashSet<string>(StringComparer.Ordinal);
        var expectedKanban = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in newRecords)
        {
            allowed.Add(record.SourceKey);
            if (!committed.Contains(record.SourceKey))
            {
                continue;
            }
            switch (ImportCommitter.TargetFor(record))
            {
                case ImportTargetKind.GcMessage:
                    expectedGc.Add(record.SourceKey);
                    break;
                case ImportTargetKind.KanbanTask:
                    expectedKanban.Add(record.SourceKey);
                    break;
                default:
                    expectedVault.Add(record.SourceKey);
                    break;
            }
        }

        var gcRead = await writer.ReadGcSourceKeysAsync(jobId, ct);
        var vaultRead = await writer.ReadVaultSourceKeysAsync(jobId, ct);
        var kanbanRead = await writer.ReadKanbanSourceKeysAsync(jobId, ct);
        Compare("gc", expectedGc, gcRead, allowed, problems);
        Compare("vault", expectedVault, vaultRead, allowed, problems);
        Compare("kanban", expectedKanban, kanbanRead, allowed, problems);
        return problems;
    }

    private static void Compare(string target, HashSet<string> expected, IReadOnlyList<string> actual, HashSet<string> allowed, List<string> problems)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in actual)
        {
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var key in expected.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!counts.ContainsKey(key))
            {
                problems.Add("missing " + target + " record " + key);
            }
        }
        foreach (var (key, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.Contains(key))
            {
                problems.Add("unexpected " + target + " record " + key);
            }
            else if (count > 1)
            {
                problems.Add("duplicate " + target + " record " + key);
            }
        }
    }
}

/// <summary>
/// Kanban task target for the commit path (doc 44 section 8.2). The Kanban module owns task creation, so the concrete
/// adapter sits outside this module. A task needs an origin GC message, and the adapter decides which one.
/// </summary>
public interface IKanbanImportTarget
{
    /// <summary>Creates one batch of tasks. Throws if the batch cannot be written and records nothing for that call.</summary>
    Task AppendBatchAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct);

    /// <summary>Source keys of tasks this job created.</summary>
    Task<IReadOnlyList<string>> ReadSourceKeysAsync(string jobId, CancellationToken ct);
}
