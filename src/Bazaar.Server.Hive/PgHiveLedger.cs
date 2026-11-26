using Bazaar.Server.Hive.Abstractions;
using Npgsql;

namespace Bazaar.Server.Hive;

/// <summary>
/// Postgres held-copy ledger (<c>hive_held_copy</c> and <c>hive_copy</c>, migration components/hive/0001). Same behavior as
/// <see cref="InMemoryHiveCopyLedger"/>: recording a copy for the same path, target and origin replaces the earlier record, and
/// revoking returns the records as they were before. Revoking never deletes rows.
/// </summary>
public sealed class PgHiveLedger(NpgsqlDataSource dataSource) : IHiveCopyLedger
{
    /// <inheritdoc />
    public async Task AddPendingAsync(HivePendingCopy pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO hive_held_copy (id, origin_project, origin_note, kind, target_project, slug, title, body, origin_tier,
                                        submitted_at, release_at)
            VALUES (@id, @origin_project, @origin_note, @kind, @target_project, @slug, @title, @body, @origin_tier,
                    @submitted_at, @release_at)
            """);
        cmd.Parameters.AddWithValue("id", pending.Id);
        cmd.Parameters.AddWithValue("origin_project", pending.Lesson.OriginProject);
        cmd.Parameters.AddWithValue("origin_note", pending.Lesson.OriginNote);
        cmd.Parameters.AddWithValue("kind", HiveKinds.Name(pending.Lesson.Kind));
        cmd.Parameters.AddWithValue("target_project", pending.TargetProject);
        cmd.Parameters.AddWithValue("slug", pending.Slug);
        cmd.Parameters.AddWithValue("title", pending.Title);
        cmd.Parameters.AddWithValue("body", pending.Body);
        cmd.Parameters.AddWithValue("origin_tier", pending.OriginTier);
        cmd.Parameters.AddWithValue("submitted_at", pending.SubmittedAt.ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("release_at", pending.ReleaseAt?.ToUniversalTime()));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HivePendingCopy>> ListPendingAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, origin_project, origin_note, kind, target_project, slug, title, body, origin_tier, submitted_at, release_at
            FROM hive_held_copy ORDER BY seq
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var held = new List<HivePendingCopy>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var lesson = new LessonRef(reader.GetString(1), reader.GetString(2), PgHiveGrantStore.ParseKind(reader.GetString(3)));
            held.Add(new HivePendingCopy(
                reader.GetGuid(0),
                lesson,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return held;
    }

    /// <inheritdoc />
    public async Task<bool> RemovePendingAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM hive_held_copy WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task RecordCopyAsync(HiveCopyRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        // The in-memory ledger removes the earlier record and appends, so the upsert refreshes seq to keep the same list order.
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO hive_copy (path, origin_project, origin_note, kind, target_project, revoked)
            VALUES (@path, @origin_project, @origin_note, @kind, @target_project, @revoked)
            ON CONFLICT (path, target_project, origin_project, origin_note)
            DO UPDATE SET kind = EXCLUDED.kind, revoked = EXCLUDED.revoked, seq = nextval('hive_copy_seq')
            """);
        cmd.Parameters.AddWithValue("path", record.Path);
        cmd.Parameters.AddWithValue("origin_project", record.Lesson.OriginProject);
        cmd.Parameters.AddWithValue("origin_note", record.Lesson.OriginNote);
        cmd.Parameters.AddWithValue("kind", HiveKinds.Name(record.Lesson.Kind));
        cmd.Parameters.AddWithValue("target_project", record.TargetProject);
        cmd.Parameters.AddWithValue("revoked", record.Revoked);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HiveCopyRecord>> ListCopiesAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT path, origin_project, origin_note, kind, target_project, revoked, seq FROM hive_copy");
        return await ReadCopiesAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HiveCopyRecord>> MarkRevokedAsync(string fromProject, string toProject, HiveKind kind, CancellationToken ct)
    {
        // One statement, so concurrent revokes cannot both report the same record. Only non-revoked rows match, so the
        // previous Revoked value of every returned row is false.
        await using var cmd = dataSource.CreateCommand("""
            UPDATE hive_copy SET revoked = true
            WHERE NOT revoked AND origin_project = @from AND target_project = @to AND kind = @kind
            RETURNING path, origin_project, origin_note, kind, target_project, false AS revoked, seq
            """);
        cmd.Parameters.AddWithValue("from", fromProject);
        cmd.Parameters.AddWithValue("to", toProject);
        cmd.Parameters.AddWithValue("kind", HiveKinds.Name(kind));
        return await ReadCopiesAsync(cmd, ct).ConfigureAwait(false);
    }

    // Column 6 is seq. Rows are sorted on it because UPDATE ... RETURNING has no defined order and a plain SELECT is not
    // guaranteed one either.
    private static async Task<IReadOnlyList<HiveCopyRecord>> ReadCopiesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<(long Seq, HiveCopyRecord Record)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var lesson = new LessonRef(reader.GetString(1), reader.GetString(2), PgHiveGrantStore.ParseKind(reader.GetString(3)));
            rows.Add((reader.GetInt64(6), new HiveCopyRecord(reader.GetString(0), lesson, reader.GetString(4), reader.GetBoolean(5))));
        }

        return [.. rows.OrderBy(r => r.Seq).Select(r => r.Record)];
    }
}
