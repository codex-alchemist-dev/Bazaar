using System.Collections.Immutable;
using Bazaar.Diagnostics.Grouping;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Diagnostics.Persistence;

/// <summary>
/// Postgres storage for crash reports, groups and the filing outbox (doc 43 section 12.1 step 7). A commit is one
/// transaction: the group row is changed under an optimistic version check, then the report, screenshot, outbox rows and
/// filing-budget rows are written. A lost race or a retried report writes nothing.
/// </summary>
public sealed class PostgresCrashStore(NpgsqlDataSource dataSource) : ICrashIntakeStore, ICrashActionOutbox
{
    private const string _groupColumns =
        "fingerprint, version, status, occurrence_count, first_seen_version, last_seen_version, first_seen_at, last_seen_at, " +
        "device_classes, os_families, issue_number, issue_closed, fixed_in_version, last_comment_date, facts::text";

    /// <inheritdoc />
    public async Task<StoredCrashGroup?> GetGroupAsync(string fingerprint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        await using var cmd = dataSource.CreateCommand($"SELECT {_groupColumns} FROM crash_group WHERE fingerprint = $1");
        cmd.Parameters.AddWithValue(fingerprint);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadGroup(r) : null;
    }

    /// <inheritdoc />
    public async Task<int> CountIssueFilingsAsync(DateOnly utcDay, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM crash_issue_filing WHERE utc_day = $1");
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = utcDay });
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingFileIssueAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM crash_action_outbox WHERE fingerprint = $1 AND kind = 'file_issue' AND NOT dead)");
        cmd.Parameters.AddWithValue(fingerprint);
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredCrashGroup>> ListDeferredGroupsAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_groupColumns} FROM crash_group WHERE status = 'FilingDeferred' ORDER BY first_seen_at, fingerprint LIMIT $1");
        cmd.Parameters.AddWithValue(max);
        var list = new List<StoredCrashGroup>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadGroup(r));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<CrashCommitStatus> CommitAsync(CrashCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var fingerprint = commit.Group.State.Fingerprint;

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (commit.Report is { } early && await ReportExistsAsync(conn, tx, early.ReportId, ct).ConfigureAwait(false))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return CrashCommitStatus.DuplicateReport;
        }

        if (!await WriteGroupAsync(conn, tx, commit, ct).ConfigureAwait(false))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return CrashCommitStatus.VersionConflict;
        }

        if (commit.Report is { } report)
        {
            if (!await InsertReportAsync(conn, tx, report, ct).ConfigureAwait(false))
            {
                // A concurrent retry won the insert between the check above and here.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return CrashCommitStatus.DuplicateReport;
            }

            if (commit.Attachment is { } attachment)
            {
                await InsertAttachmentAsync(conn, tx, attachment, ct).ConfigureAwait(false);
            }
        }

        foreach (var row in commit.Outbox)
        {
            await InsertOutboxAsync(conn, tx, row, fingerprint, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return CrashCommitStatus.Committed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingCrashAction>> GetDueAsync(DateTimeOffset now, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, fingerprint, kind, payload::text, created_at, attempts, not_before FROM crash_action_outbox " +
            "WHERE NOT dead AND not_before <= $1 ORDER BY created_at, id LIMIT $2");
        cmd.Parameters.AddWithValue(Micro(now));
        cmd.Parameters.AddWithValue(max);
        var list = new List<PendingCrashAction>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var fingerprint = r.GetString(1);
            list.Add(new PendingCrashAction(
                r.GetGuid(0),
                fingerprint,
                CrashJson.ReadAction(r.GetString(2), fingerprint, r.GetString(3)),
                r.GetFieldValue<DateTimeOffset>(4),
                r.GetInt32(5),
                r.GetFieldValue<DateTimeOffset>(6)));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM crash_action_outbox WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FailAsync(Guid id, DateTimeOffset notBefore, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        await using var cmd = dataSource.CreateCommand(
            "UPDATE crash_action_outbox SET attempts = attempts + 1, not_before = $2, last_error = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(Micro(notBefore));
        cmd.Parameters.AddWithValue(reason);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeadLetterAsync(Guid id, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        await using var cmd = dataSource.CreateCommand(
            "UPDATE crash_action_outbox SET dead = true, attempts = attempts + 1, last_error = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(reason);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReportExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid reportId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM crash_report WHERE report_id = $1)", conn, tx);
        cmd.Parameters.AddWithValue(reportId);
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task<bool> WriteGroupAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CrashCommit commit, CancellationToken ct)
    {
        var s = commit.Group.State;
        var facts = CrashJson.WriteFacts(commit.Group.Facts);
        var insert = commit.ExpectedGroupVersion is null;
        var sql = insert
            ? "INSERT INTO crash_group (fingerprint, version, status, occurrence_count, first_seen_version, last_seen_version, " +
              "first_seen_at, last_seen_at, device_classes, os_families, issue_number, issue_closed, fixed_in_version, " +
              "last_comment_date, facts) VALUES ($1, 1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14::jsonb) " +
              "ON CONFLICT (fingerprint) DO NOTHING"
            : "UPDATE crash_group SET version = version + 1, status = $2, occurrence_count = $3, first_seen_version = $4, " +
              "last_seen_version = $5, first_seen_at = $6, last_seen_at = $7, device_classes = $8, os_families = $9, " +
              "issue_number = $10, issue_closed = $11, fixed_in_version = $12, last_comment_date = $13, facts = $14::jsonb " +
              "WHERE fingerprint = $1 AND version = $15";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue(s.Fingerprint);
        cmd.Parameters.AddWithValue(s.Status.ToString());
        cmd.Parameters.AddWithValue(s.OccurrenceCount);
        cmd.Parameters.AddWithValue(s.FirstSeenVersion);
        cmd.Parameters.AddWithValue(s.LastSeenVersion);
        cmd.Parameters.AddWithValue(Micro(s.FirstSeenAt));
        cmd.Parameters.AddWithValue(Micro(s.LastSeenAt));
        cmd.Parameters.AddWithValue(s.DeviceClasses.ToArray());
        cmd.Parameters.AddWithValue(s.OsFamilies.ToArray());
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)s.IssueNumber ?? DBNull.Value });
        cmd.Parameters.AddWithValue(s.IssueClosed);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)s.FixedInVersion ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)s.LastCommentDate ?? DBNull.Value });
        cmd.Parameters.AddWithValue(facts);
        if (!insert)
        {
            cmd.Parameters.AddWithValue(commit.ExpectedGroupVersion!.Value);
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> InsertReportAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StoredCrashReport r, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO crash_report (report_id, install_id, fingerprint, created_at, received_at, client_version, symbols, " +
            "body_sha256, key_id, nonce, ciphertext, tag) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) " +
            "ON CONFLICT (report_id) DO NOTHING", conn, tx);
        cmd.Parameters.AddWithValue(r.ReportId);
        cmd.Parameters.AddWithValue(r.InstallId);
        cmd.Parameters.AddWithValue(r.Fingerprint);
        cmd.Parameters.AddWithValue(Micro(r.CreatedAt));
        cmd.Parameters.AddWithValue(Micro(r.ReceivedAt));
        cmd.Parameters.AddWithValue(r.ClientVersion);
        cmd.Parameters.AddWithValue(r.Symbols.ToString());
        cmd.Parameters.AddWithValue(r.BodySha256);
        cmd.Parameters.AddWithValue(r.Body.KeyId);
        cmd.Parameters.AddWithValue(r.Body.Nonce);
        cmd.Parameters.AddWithValue(r.Body.Ciphertext);
        cmd.Parameters.AddWithValue(r.Body.Tag);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task InsertAttachmentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StoredCrashAttachment a, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO crash_attachment (attachment_id, report_id, content_type, sha256, key_id, nonce, ciphertext, tag) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", conn, tx);
        cmd.Parameters.AddWithValue(a.AttachmentId);
        cmd.Parameters.AddWithValue(a.ReportId);
        cmd.Parameters.AddWithValue(a.ContentType);
        cmd.Parameters.AddWithValue(a.Sha256);
        cmd.Parameters.AddWithValue(a.Data.KeyId);
        cmd.Parameters.AddWithValue(a.Data.Nonce);
        cmd.Parameters.AddWithValue(a.Data.Ciphertext);
        cmd.Parameters.AddWithValue(a.Data.Tag);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PendingCrashAction row, string fingerprint, CancellationToken ct)
    {
        var (kind, payload) = CrashJson.WriteAction(row.Action);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO crash_action_outbox (id, fingerprint, kind, payload, created_at, attempts, not_before) " +
            "VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7)", conn, tx))
        {
            cmd.Parameters.AddWithValue(row.Id);
            cmd.Parameters.AddWithValue(fingerprint);
            cmd.Parameters.AddWithValue(kind);
            cmd.Parameters.AddWithValue(payload);
            cmd.Parameters.AddWithValue(Micro(row.CreatedAt));
            cmd.Parameters.AddWithValue(row.Attempts);
            cmd.Parameters.AddWithValue(Micro(row.NotBefore));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (kind == CrashJson.KindFileIssue)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO crash_issue_filing (id, fingerprint, utc_day, decided_at) VALUES ($1, $2, $3, $4)", conn, tx);
            cmd.Parameters.AddWithValue(row.Id);
            cmd.Parameters.AddWithValue(fingerprint);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Date,
                Value = DateOnly.FromDateTime(row.CreatedAt.UtcDateTime),
            });
            cmd.Parameters.AddWithValue(Micro(row.CreatedAt));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static StoredCrashGroup ReadGroup(NpgsqlDataReader r)
    {
        var state = new GroupState
        {
            Fingerprint = r.GetString(0),
            Status = Enum.Parse<GroupStatus>(r.GetString(2)),
            OccurrenceCount = r.GetInt64(3),
            FirstSeenVersion = r.GetString(4),
            LastSeenVersion = r.GetString(5),
            FirstSeenAt = r.GetFieldValue<DateTimeOffset>(6),
            LastSeenAt = r.GetFieldValue<DateTimeOffset>(7),
            DeviceClasses = r.GetFieldValue<string[]>(8).ToImmutableSortedSet(StringComparer.Ordinal),
            OsFamilies = r.GetFieldValue<string[]>(9).ToImmutableSortedSet(StringComparer.Ordinal),
            IssueNumber = r.IsDBNull(10) ? null : r.GetInt32(10),
            IssueClosed = r.GetBoolean(11),
            FixedInVersion = r.IsDBNull(12) ? null : r.GetString(12),
            LastCommentDate = r.IsDBNull(13) ? null : r.GetFieldValue<DateOnly>(13),
        };
        return new StoredCrashGroup(state, CrashJson.ReadFacts(r.GetString(14)), r.GetInt64(1));
    }

    /// <summary>Postgres keeps microseconds; truncating here makes a value read back equal to the value written.</summary>
    private static DateTimeOffset Micro(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);
}
