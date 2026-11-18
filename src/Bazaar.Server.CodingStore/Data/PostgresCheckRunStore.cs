using Bazaar.Contracts.Coding;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.CodingStore.Data;

/// <summary>SQL-first store over the insert-only <c>check_runs</c> table (migration coding/0002, doc 17 WP2).</summary>
public sealed class PostgresCheckRunStore(NpgsqlDataSource dataSource) : ICheckRunStore
{
    private const string _columns =
        "id, commit_sha, base_commit, build_exit_code, tests_passed, tests_failed, tests_skipped, test_count_base, flags, ran_at";

    private const string _insertSql = """
        INSERT INTO check_runs (id, commit_sha, base_commit, build_exit_code, tests_passed, tests_failed, tests_skipped,
            test_count_base, flags, ran_at)
        VALUES (@id, @commit_sha, @base_commit, @build_exit_code, @tests_passed, @tests_failed, @tests_skipped,
            @test_count_base, @flags, @ran_at)
        """;

    /// <inheritdoc />
    public async ValueTask AppendAsync(CheckRunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("commit_sha", record.Commit);
        cmd.Parameters.AddWithValue("base_commit", (object?)record.BaseCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("build_exit_code", (object?)record.BuildExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tests_passed", (object?)record.TestsPassed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tests_failed", (object?)record.TestsFailed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tests_skipped", (object?)record.TestsSkipped ?? DBNull.Value);
        cmd.Parameters.AddWithValue("test_count_base", (object?)record.TestCountBase ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("flags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = record.Flags.ToArray() });
        cmd.Parameters.AddWithValue("ran_at", record.RanAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<CheckRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM check_runs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CheckRunRecord>> ListByCommitAsync(string commit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM check_runs WHERE commit_sha = @commit_sha ORDER BY ran_at DESC, id");
        cmd.Parameters.AddWithValue("commit_sha", commit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<CheckRunRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(Map(reader));
        return rows;
    }

    private static CheckRunRecord Map(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetInt32(3),
        r.IsDBNull(4) ? null : r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetInt32(7),
        r.GetFieldValue<string[]>(8),
        r.GetFieldValue<DateTimeOffset>(9));
}
