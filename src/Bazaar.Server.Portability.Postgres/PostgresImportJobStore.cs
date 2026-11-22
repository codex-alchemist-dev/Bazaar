using System.Buffers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>
/// Durable <see cref="IImportJobStore"/> over <c>import_job</c>, <c>import_batch</c> and <c>import_item</c> (migration
/// <c>portability/0001_import_job.sql</c>, doc 44 sections 8.4, 8.7 and 8.9). A batch and its items commit in one transaction,
/// so a resume sees either the whole batch or none of it. Timestamps are kept to the microsecond, which is what the database stores.
/// </summary>
public sealed class PostgresImportJobStore(NpgsqlDataSource dataSource) : IImportJobStore
{
    private const string _uniqueViolation = "23505";
    private const string _insertJob =
        "INSERT INTO import_job (job_id, actor_id, importer_id, importer_version, state, max_upload_bytes, source_manifest, created_at) " +
        "VALUES ($1, $2, $3, $4, 'staged', $5, $6::jsonb, $7)";
    private const string _jobColumns =
        "job_id, actor_id, importer_id, importer_version, state, max_upload_bytes, source_manifest::text, mapping_hash, dry_run_hash, " +
        "created_at, started_at, last_progress_at, ended_at, failure_reason";
    private const string _getJob = "SELECT " + _jobColumns + " FROM import_job WHERE job_id = $1";
    private const string _recordDryRun =
        "UPDATE import_job SET mapping_hash = $2, dry_run_hash = $3 WHERE job_id = $1 AND state IN ('staged', 'dry_run')";
    private const string _transition =
        "UPDATE import_job SET state = $2, started_at = COALESCE(started_at, $3), ended_at = COALESCE($4, ended_at), " +
        "failure_reason = COALESCE($5, failure_reason) WHERE job_id = $1 AND state = ANY($6)";
    private const string _lockState = "SELECT state FROM import_job WHERE job_id = $1 FOR UPDATE";
    private const string _insertBatch =
        "INSERT INTO import_batch (job_id, batch_number, item_counts, first_source_key, last_source_key, batch_hash, committed_at) " +
        "VALUES ($1, $2, $3::jsonb, $4, $5, $6, $7)";
    private const string _insertItem =
        "INSERT INTO import_item (importer_id, source_key, content_hash, target_kind, target_id, target_version, job_id, batch_number, recorded_at) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)";
    private const string _progress = "UPDATE import_job SET last_progress_at = $2 WHERE job_id = $1";
    private const string _listBatches =
        "SELECT job_id, batch_number, item_counts::text, first_source_key, last_source_key, batch_hash, committed_at " +
        "FROM import_batch WHERE job_id = $1 ORDER BY batch_number";
    private const string _committedKeys = "SELECT DISTINCT source_key FROM import_item WHERE job_id = $1";
    private const string _latestItem =
        "SELECT importer_id, source_key, content_hash, target_kind, target_id, target_version, job_id FROM import_item " +
        "WHERE importer_id = $1 AND source_key = $2 ORDER BY recorded_at DESC LIMIT 1";

    /// <inheritdoc />
    public async Task CreateJobAsync(NewImportJob job, CancellationToken ct)
    {
        ImportStoreRules.ValidateNewJob(job);
        await using var cmd = dataSource.CreateCommand(_insertJob);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = job.JobId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = job.ActorId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = job.ImporterId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = job.ImporterVersion });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = job.MaxUploadBytes });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ManifestJson(job.SourceManifest) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Micros(job.CreatedAt) });
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == _uniqueViolation)
        {
            throw new ImportStoreConflictException("Import job " + job.JobId + " already exists.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<ImportJobRecord?> GetJobAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await using var cmd = dataSource.CreateCommand(_getJob);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ImportJobRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseState(reader.GetString(4)),
            reader.GetInt64(5),
            ParseManifest(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    /// <inheritdoc />
    public async Task<bool> RecordDryRunAsync(string jobId, string mappingHash, string dryRunHash, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        if (!ImportStoreRules.IsSha256Hex(mappingHash) || !ImportStoreRules.IsSha256Hex(dryRunHash))
        {
            throw new ArgumentException("Mapping and dry-run hashes must be SHA-256 digests in lowercase hex.");
        }
        await using var cmd = dataSource.CreateCommand(_recordDryRun);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = mappingHash });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = dryRunHash });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TransitionAsync(string jobId, ImportJobState target, DateTimeOffset at, string? reason, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        var stamp = Micros(at);
        await using var cmd = dataSource.CreateCommand(_transition);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = StateName(target) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = target == ImportJobState.Committing ? stamp : null });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = ImportJobStates.IsTerminal(target) ? stamp : null });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = reason });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. ImportJobStates.SourcesFor(target).Select(StateName)] });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task RecordBatchAsync(ImportBatchRecord batch, IReadOnlyList<ImportItemRecord> items, CancellationToken ct)
    {
        ImportStoreRules.ValidateBatch(batch, items);
        var committedAt = Micros(batch.CommittedAt);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // The row lock serialises batches of one job and keeps a batch out of a job that is not committing.
            await using (var lockCmd = new NpgsqlCommand(_lockState, conn, tx))
            {
                lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = batch.JobId });
                var state = await lockCmd.ExecuteScalarAsync(ct) as string
                    ?? throw new InvalidOperationException("No import job " + batch.JobId + ".");
                if (ParseState(state) != ImportJobState.Committing)
                {
                    throw new InvalidOperationException("Import job " + batch.JobId + " is " + state + ", not committing; the batch is refused.");
                }
            }

            await using (var batchCmd = new NpgsqlCommand(_insertBatch, conn, tx))
            {
                batchCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = batch.JobId });
                batchCmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batch.BatchNumber });
                batchCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = CountsJson(batch.ItemCounts) });
                batchCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = batch.FirstSourceKey });
                batchCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = batch.LastSourceKey });
                batchCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = batch.BatchHash });
                batchCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = committedAt });
                await batchCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var itemBatch = conn.CreateBatch())
            {
                itemBatch.Transaction = tx;
                foreach (var item in items)
                {
                    var command = new NpgsqlBatchCommand(_insertItem);
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.ImporterId });
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.SourceKey });
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.ContentHash });
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = TargetName(item.TargetKind) });
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.TargetId });
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.TargetVersion });
                    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.JobId });
                    command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batch.BatchNumber });
                    command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = committedAt });
                    itemBatch.BatchCommands.Add(command);
                }
                await itemBatch.ExecuteNonQueryAsync(ct);
            }

            await using (var progressCmd = new NpgsqlCommand(_progress, conn, tx))
            {
                progressCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = batch.JobId });
                progressCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = committedAt });
                await progressCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == _uniqueViolation)
        {
            throw new ImportStoreConflictException(
                "Batch " + batch.BatchNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of job " + batch.JobId
                + " repeats a batch number or an item that is already recorded; nothing was written.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportBatchRecord>> ListBatchesAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await using var cmd = dataSource.CreateCommand(_listBatches);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var batches = new List<ImportBatchRecord>();
        while (await reader.ReadAsync(ct))
        {
            batches.Add(new ImportBatchRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                ParseCounts(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return batches;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await using var cmd = dataSource.CreateCommand(_committedKeys);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    /// <inheritdoc />
    public async Task<ImportItemRecord?> GetLatestItemAsync(string importerId, string sourceKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        await using var cmd = dataSource.CreateCommand(_latestItem);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = importerId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sourceKey });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ImportItemRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTargetKind(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    /// <summary>Truncates to the microsecond, the precision of <c>timestamptz</c>, so a read returns what a write stored.</summary>
    private static DateTimeOffset Micros(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, value.Offset);

    /// <summary>Writes the manifest as a JSON array. The writer escapes file names, so no name is pasted into the text.</summary>
    private static string ManifestJson(IReadOnlyList<ImportManifestFile> files)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var file in files)
            {
                writer.WriteStartObject();
                writer.WriteString("name", file.Name);
                writer.WriteNumber("size_bytes", file.SizeBytes);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static List<ImportManifestFile> ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var files = new List<ImportManifestFile>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            files.Add(new ImportManifestFile(
                element.GetProperty("name").GetString() ?? throw new InvalidDataException("Manifest entry without a name."),
                element.GetProperty("size_bytes").GetInt64(),
                element.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Manifest entry without a digest.")));
        }
        return files;
    }

    /// <summary>Writes item counts as a JSON object keyed by target name.</summary>
    private static string CountsJson(IReadOnlyDictionary<ImportTargetKind, int> counts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var kind in counts.Keys.OrderBy(k => k))
            {
                writer.WriteNumber(TargetName(kind), counts[kind]);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<ImportTargetKind, int> ParseCounts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var counts = new Dictionary<ImportTargetKind, int>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            counts[ParseTargetKind(property.Name)] = property.Value.GetInt32();
        }
        return counts;
    }

    /// <summary>The stored name of a target kind. Adding a kind needs a migration that widens the CHECK on <c>import_item</c>.</summary>
    private static string TargetName(ImportTargetKind kind) => kind switch
    {
        ImportTargetKind.GcMessage => "gc_message",
        ImportTargetKind.VaultFile => "vault_file",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No stored name for this target kind."),
    };

    private static ImportTargetKind ParseTargetKind(string name) => name switch
    {
        "gc_message" => ImportTargetKind.GcMessage,
        "vault_file" => ImportTargetKind.VaultFile,
        _ => throw new InvalidDataException("Unknown import target kind '" + name + "'."),
    };

    private static string StateName(ImportJobState state) => state switch
    {
        ImportJobState.Staged => "staged",
        ImportJobState.DryRun => "dry_run",
        ImportJobState.Committing => "committing",
        ImportJobState.Paused => "paused",
        ImportJobState.Stalled => "stalled",
        ImportJobState.Verifying => "verifying",
        ImportJobState.Verified => "verified",
        ImportJobState.FailedVerify => "failed_verify",
        ImportJobState.Failed => "failed",
        ImportJobState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No stored name for this job state."),
    };

    private static ImportJobState ParseState(string name) => name switch
    {
        "staged" => ImportJobState.Staged,
        "dry_run" => ImportJobState.DryRun,
        "committing" => ImportJobState.Committing,
        "paused" => ImportJobState.Paused,
        "stalled" => ImportJobState.Stalled,
        "verifying" => ImportJobState.Verifying,
        "verified" => ImportJobState.Verified,
        "failed_verify" => ImportJobState.FailedVerify,
        "failed" => ImportJobState.Failed,
        "cancelled" => ImportJobState.Cancelled,
        _ => throw new InvalidDataException("Unknown import job state '" + name + "'."),
    };
}
