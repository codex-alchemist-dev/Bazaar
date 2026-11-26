using System.Globalization;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>One staged payload file of an import job (table <c>import_staging</c>).</summary>
/// <param name="RelativePath">Path under the job's staging directory. Never rooted and never contains a <c>..</c> segment.</param>
public sealed record ImportStagingEntry(string JobId, string SourceKey, string RelativePath, long SizeBytes, string Sha256, DateTimeOffset StagedAt);

/// <summary>
/// Record side of staging (doc 44 sections 8.6 and 8.7, PROPOSED) over <c>import_staging</c> (migration
/// <c>portability/0002_import_staging_withdrawal.sql</c>). File deletion is injected, so this project holds no file code:
/// <see cref="DeleteAsync"/> removes the job's files first and then marks the rows deleted. A failed file delete leaves the rows
/// live, so the next sweep retries. Rows are kept as history.
/// </summary>
public sealed class PostgresImportStagingStore(
    NpgsqlDataSource dataSource,
    Func<string, CancellationToken, Task> deleteJobFiles,
    TimeProvider? time = null) : IImportStagingStore
{
    private const string _record =
        "INSERT INTO import_staging (job_id, source_key, relative_path, size_bytes, sha256, staged_at) VALUES ($1, $2, $3, $4, $5, $6) " +
        "ON CONFLICT (job_id, source_key) DO UPDATE SET relative_path = EXCLUDED.relative_path, size_bytes = EXCLUDED.size_bytes, " +
        "sha256 = EXCLUDED.sha256, staged_at = EXCLUDED.staged_at, deleted_at = NULL";
    private const string _list =
        "SELECT job_id, source_key, relative_path, size_bytes, sha256, staged_at FROM import_staging " +
        "WHERE job_id = $1 AND deleted_at IS NULL ORDER BY source_key";
    private const string _liveBytes = "SELECT COALESCE(sum(size_bytes), 0)::bigint FROM import_staging WHERE job_id = $1 AND deleted_at IS NULL";
    private const string _listVerified =
        "SELECT j.job_id, j.ended_at FROM import_job j WHERE j.state = 'verified' " +
        "AND EXISTS (SELECT 1 FROM import_staging s WHERE s.job_id = j.job_id AND s.deleted_at IS NULL) ORDER BY j.ended_at, j.job_id";
    private const string _markDeleted = "UPDATE import_staging SET deleted_at = $2 WHERE job_id = $1 AND deleted_at IS NULL";
    private const string _foreignKeyViolation = "23503";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Records one staged file. A repeat for the same key replaces the row, and undoes a delete mark.</summary>
    /// <exception cref="ImportStoreConflictException">The job does not exist.</exception>
    public async Task RecordAsync(ImportStagingEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ImportStoreRules.ValidateJobId(entry.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourceKey);
        ValidateRelativePath(entry.RelativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.SizeBytes);
        if (!ImportStoreRules.IsSha256Hex(entry.Sha256))
        {
            throw new ArgumentException("The staged file hash must be a SHA-256 digest in lowercase hex.", nameof(entry));
        }

        await using var cmd = dataSource.CreateCommand(_record);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.JobId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.SourceKey });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.RelativePath });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = entry.SizeBytes });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entry.Sha256 });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Micros(entry.StagedAt) });
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == _foreignKeyViolation)
        {
            throw new ImportStoreConflictException("Import job " + entry.JobId + " does not exist.", ex);
        }
    }

    /// <summary>The job's staged files that are not deleted, in source key order.</summary>
    public async Task<IReadOnlyList<ImportStagingEntry>> ListAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await using var cmd = dataSource.CreateCommand(_list);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ImportStagingEntry>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ImportStagingEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return list;
    }

    /// <summary>
    /// Bytes the job holds in staging now, for the quota of section 8.7. The caller adds the next file's size and compares it with its limit.
    /// </summary>
    public async Task<long> StagedBytesAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await using var cmd = dataSource.CreateCommand(_liveBytes);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string JobId, DateTimeOffset VerifiedAt)>> ListVerifiedAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_listVerified);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(string, DateTimeOffset)>();
        while (await reader.ReadAsync(ct))
        {
            list.Add((reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }
        return list;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await deleteJobFiles(jobId, ct);
        await using var cmd = dataSource.CreateCommand(_markDeleted);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Micros(_time.GetUtcNow()) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] is '/' or '\\' || path.Split('/', '\\').Contains(".."))
        {
            throw new ArgumentException("A staged path must be relative and stay under the job's staging directory.", nameof(path));
        }
    }

    private static DateTimeOffset Micros(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, value.Offset);
}
