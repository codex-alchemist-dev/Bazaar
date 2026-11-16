namespace Bazaar.Portability;

/// <summary>
/// The real target writer: GC entries through <see cref="GcImportTarget"/>, Vault files through <see cref="VaultImportTarget"/>.
/// </summary>
public sealed class ImportTargetWriter(GcImportTarget gc, VaultImportTarget vault) : IImportTargetWriter
{
    public async Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        keys.UnionWith(await gc.ReadSourceKeysAsync(jobId, ct));
        keys.UnionWith(await vault.ReadSourceKeysAsync(jobId, ct));
        return keys;
    }

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
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in newRecords)
        {
            allowed.Add(record.SourceKey);
            if (!committed.Contains(record.SourceKey))
            {
                continue;
            }
            if (ImportCommitter.TargetFor(record) == ImportTargetKind.GcMessage)
            {
                expectedGc.Add(record.SourceKey);
            }
            else
            {
                expectedVault.Add(record.SourceKey);
            }
        }

        var gcRead = await writer.ReadGcSourceKeysAsync(jobId, ct);
        var vaultRead = await writer.ReadVaultSourceKeysAsync(jobId, ct);
        Compare("gc", expectedGc, gcRead, allowed, problems);
        Compare("vault", expectedVault, vaultRead, allowed, problems);
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
