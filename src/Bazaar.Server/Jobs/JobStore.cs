using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Jobs;

/// <summary>SQL-first access to the <c>job</c> table (doc 03). Every lease and due time is compared with database <c>now()</c>.</summary>
public sealed class JobStore(NpgsqlDataSource dataSource)
{
    private const int _maxErrorLength = 2000;

    private const string _enqueueSql = """
        INSERT INTO job (kind, dedupe_key, run_at, state, payload)
        VALUES (@kind, @dedupe_key, COALESCE(@run_at, now()), 'queued', @payload::jsonb)
        ON CONFLICT (dedupe_key) DO NOTHING
        RETURNING id
        """;

    private const string _failExhaustedSql = """
        UPDATE job
        SET state = 'failed', lease_owner = NULL, lease_until = NULL,
            last_error = 'lease expired on the final attempt', updated_at = now()
        WHERE state = 'running' AND lease_until <= now() AND attempts >= @max_attempts
        """;

    private const string _claimSql = """
        WITH due AS (
            SELECT id FROM job
            WHERE (state = 'queued' AND run_at <= now())
               OR (state = 'running' AND lease_until <= now())
            ORDER BY run_at, id
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
        )
        UPDATE job AS j
        SET state = 'running', lease_owner = @owner,
            lease_until = now() + make_interval(secs => @lease_seconds),
            attempts = j.attempts + 1, updated_at = now()
        FROM due
        WHERE j.id = due.id
        RETURNING j.id, j.kind, j.dedupe_key, j.attempts, j.payload::text
        """;

    private const string _completeSql = """
        UPDATE job
        SET state = 'succeeded', lease_owner = NULL, lease_until = NULL, last_error = NULL, updated_at = now()
        WHERE id = @id AND state = 'running' AND lease_owner = @owner
        """;

    private const string _failSql = """
        UPDATE job
        SET state = CASE WHEN attempts >= @max_attempts THEN 'failed' ELSE 'queued' END,
            run_at = now() + make_interval(secs => @delay_seconds),
            lease_owner = NULL, lease_until = NULL,
            last_error = @last_error, updated_at = now()
        WHERE id = @id AND state = 'running' AND lease_owner = @owner
        """;

    /// <summary>Adds a job. Returns its id, or null when <paramref name="dedupeKey"/> was used before (the earlier job wins).</summary>
    public async Task<long?> EnqueueAsync(
        string kind, string payloadJson, DateTimeOffset? runAt = null, string? dedupeKey = null, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_enqueueSql);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("dedupe_key", (object?)dedupeKey ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("run_at", runAt));
        cmd.Parameters.AddWithValue("payload", payloadJson);
        var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return id is null or DBNull ? null : (long)id;
    }

    /// <summary>
    /// Marks expired leases that have used every attempt as failed. Run before each claim so a crashing job cannot
    /// be retried forever. Returns how many jobs were failed.
    /// </summary>
    public async Task<int> FailExhaustedAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_failExhaustedSql);
        cmd.Parameters.AddWithValue("max_attempts", maxAttempts);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due jobs: queued jobs whose run time has come, and running jobs whose lease
    /// has expired. Each claim takes a lease for <paramref name="lease"/> and counts as one attempt.
    /// </summary>
    public async Task<IReadOnlyList<JobRecord>> ClaimDueAsync(
        string owner, TimeSpan lease, int batchSize, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_claimSql);
        cmd.Parameters.AddWithValue("batch_size", batchSize);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("lease_seconds", lease.TotalSeconds);
        var claimed = new List<JobRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            claimed.Add(new JobRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4)));
        }

        // UPDATE ... RETURNING has no order guarantee; hand jobs out oldest first.
        claimed.Sort((a, b) => a.Id.CompareTo(b.Id));
        return claimed;
    }

    /// <summary>Marks a claimed job succeeded. Returns false when the lease is no longer held by <paramref name="owner"/>.</summary>
    public async Task<bool> CompleteAsync(long id, string owner, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_completeSql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Records a failed attempt. The job is queued again after <paramref name="retryDelay"/>, or marked failed once its
    /// stored attempt count reaches <paramref name="maxAttempts"/>. Returns false when the lease is no longer held.
    /// </summary>
    public async Task<bool> FailAsync(
        long id, string owner, int maxAttempts, TimeSpan retryDelay, string error, CancellationToken ct = default)
    {
        var message = error.Length > _maxErrorLength ? error[.._maxErrorLength] : error;
        await using var cmd = dataSource.CreateCommand(_failSql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("max_attempts", maxAttempts);
        cmd.Parameters.AddWithValue("delay_seconds", retryDelay.TotalSeconds);
        cmd.Parameters.AddWithValue("last_error", message);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Reads one job. Used by tests and by the status page later.</summary>
    public async Task<JobState?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT state, attempts, lease_owner, last_error, run_at FROM job WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new JobState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }
}

/// <summary>A job row as stored, for inspection.</summary>
public sealed record JobState(string State, int Attempts, string? LeaseOwner, string? LastError, DateTimeOffset RunAt);
