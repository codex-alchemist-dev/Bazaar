using Npgsql;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>What a withdrawal touched. The wire names are the values of <c>import_withdrawal.target_kind</c>.</summary>
public enum ImportWithdrawalTargetKind
{
    GcMessage,
    VaultFile,
    KanbanTask,
}

/// <summary>One withdrawn record. For a Kanban task the <paramref name="SourceKey"/> is the task id.</summary>
public sealed record ImportWithdrawalRecord(string SourceKey, ImportWithdrawalTargetKind TargetKind, string TargetId);

/// <summary>
/// Withdrawal record and resume store (doc 44 section 8.8, PROPOSED) over <c>import_withdrawal</c> (migration
/// <c>portability/0002_import_staging_withdrawal.sql</c>). It records that a withdrawal happened. The writes that do it (a GC
/// version, a Vault commit, a Kanban transition) are made elsewhere, through their own owners, and recorded here after they succeed.
/// Rows are history and are never deleted.
/// </summary>
public sealed class PostgresImportWithdrawalStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
{
    private const string _insert =
        "INSERT INTO import_withdrawal (job_id, source_key, target_kind, target_id, withdrawn_at) VALUES ($1, $2, $3, $4, $5) " +
        "ON CONFLICT (job_id, source_key, target_kind) DO NOTHING";
    private const string _keys = "SELECT source_key FROM import_withdrawal WHERE job_id = $1 AND target_kind = ANY($2) ORDER BY source_key";
    private const string _foreignKeyViolation = "23503";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Records a batch in one transaction. A record already there is left as it was, so a repeat after a crash is safe.
    /// Returns the number of rows newly written.
    /// </summary>
    /// <exception cref="ImportStoreConflictException">The job does not exist. Nothing from the batch is kept.</exception>
    public async Task<int> RecordAsync(string jobId, IReadOnlyList<ImportWithdrawalRecord> records, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(record.SourceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.TargetId);
        }

        var at = _time.GetUtcNow();
        at = new DateTimeOffset(at.Ticks - at.Ticks % 10, at.Offset);
        var written = 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var record in records)
            {
                await using var cmd = new NpgsqlCommand(_insert, conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.SourceKey });
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = KindName(record.TargetKind) });
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.TargetId });
                cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
                written += await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == _foreignKeyViolation)
        {
            throw new ImportStoreConflictException("Import job " + jobId + " does not exist.", ex);
        }
        return written;
    }

    /// <summary>Source keys of this job already withdrawn for the given kinds.</summary>
    public async Task<IReadOnlySet<string>> ReadWithdrawnKeysAsync(string jobId, IReadOnlyList<ImportWithdrawalTargetKind> kinds, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        ArgumentNullException.ThrowIfNull(kinds);
        await using var cmd = dataSource.CreateCommand(_keys);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. kinds.Select(KindName)] });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    private static string KindName(ImportWithdrawalTargetKind kind) => kind switch
    {
        ImportWithdrawalTargetKind.GcMessage => "gc_message",
        ImportWithdrawalTargetKind.VaultFile => "vault_file",
        ImportWithdrawalTargetKind.KanbanTask => "kanban_task",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// <see cref="IImportWithdrawalWriter"/> for the GC and Vault sides. The side effect goes through its own port, and only after it
/// succeeds is the batch recorded, so the record never runs ahead of the work. A crash between the two repeats the batch, which the
/// ports must tolerate. The target id of a row is its source key, because the import item index (not this table) maps keys to objects.
/// </summary>
public sealed class PostgresImportWithdrawalWriter(PostgresImportWithdrawalStore store, IGcWithdrawalMarker gc, IVaultWithdrawalReverter vault) : IImportWithdrawalWriter
{
    private static readonly ImportWithdrawalTargetKind[] _kinds = [ImportWithdrawalTargetKind.GcMessage, ImportWithdrawalTargetKind.VaultFile];

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ReadWithdrawnSourceKeysAsync(string jobId, CancellationToken ct) =>
        store.ReadWithdrawnKeysAsync(jobId, _kinds, ct);

    /// <inheritdoc />
    public async Task WithdrawGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
    {
        await gc.MarkWithdrawnAsync(jobId, batchIndex, sourceKeys, ct);
        await store.RecordAsync(jobId, [.. sourceKeys.Select(k => new ImportWithdrawalRecord(k, ImportWithdrawalTargetKind.GcMessage, k))], ct);
    }

    /// <inheritdoc />
    public async Task RevertVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
    {
        await vault.RevertAsync(jobId, batchIndex, sourceKeys, ct);
        await store.RecordAsync(jobId, [.. sourceKeys.Select(k => new ImportWithdrawalRecord(k, ImportWithdrawalTargetKind.VaultFile, k))], ct);
    }
}
