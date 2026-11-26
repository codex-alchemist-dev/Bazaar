using Bazaar.Server.GeneticMemory.Abstractions;
using Npgsql;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// Postgres review queue (<c>gmt_candidate</c>, migration components/genetic_memory/0001). Same behavior as <see cref="InMemoryGmtLedger"/>.
/// Rows are never deleted: a rejected candidate stays for audit.
/// </summary>
public sealed class PgGmtLedger(NpgsqlDataSource dataSource) : IGmtLedger
{
    private const string _columns =
        "id, child_agent, target_agent, content_class, kind, title, body, trace_ref, tainted, required_reviewer, status, staged_at, accepted_path";

    /// <inheritdoc />
    public async Task AddAsync(GmtCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var cmd = dataSource.CreateCommand($"INSERT INTO gmt_candidate ({_columns}) VALUES (@id, @child, @target, @class, @kind, @title, @body, @trace, @tainted, @reviewer, @status, @staged, @path)");
        Bind(cmd, candidate);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GmtCandidate?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM gmt_candidate WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(GmtCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var cmd = dataSource.CreateCommand("""
            UPDATE gmt_candidate
            SET child_agent = @child, target_agent = @target, content_class = @class, kind = @kind, title = @title, body = @body,
                trace_ref = @trace, tainted = @tainted, required_reviewer = @reviewer, status = @status, staged_at = @staged,
                accepted_path = @path
            WHERE id = @id
            """);
        Bind(cmd, candidate);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GmtCandidate>> ListAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM gmt_candidate ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<GmtCandidate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    private static void Bind(NpgsqlCommand cmd, GmtCandidate c)
    {
        cmd.Parameters.AddWithValue("id", c.Id);
        cmd.Parameters.AddWithValue("child", c.ChildAgent);
        cmd.Parameters.AddWithValue("target", c.TargetAgent);
        cmd.Parameters.AddWithValue("class", c.ContentClass.ToString());
        cmd.Parameters.AddWithValue("kind", c.Kind.ToString());
        cmd.Parameters.AddWithValue("title", c.Title);
        cmd.Parameters.AddWithValue("body", c.Body);
        cmd.Parameters.Add(new NpgsqlParameter<string?>("trace", c.TraceRef));
        cmd.Parameters.AddWithValue("tainted", c.Tainted);
        cmd.Parameters.AddWithValue("reviewer", c.RequiredReviewer.ToString());
        cmd.Parameters.AddWithValue("status", c.Status.ToString());
        cmd.Parameters.AddWithValue("staged", c.StagedAt.ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter<string?>("path", c.AcceptedPath));
    }

    private static GmtCandidate Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        Enum.Parse<GmtContentClass>(r.GetString(3)),
        Enum.Parse<GmtLessonKind>(r.GetString(4)),
        r.GetString(5),
        r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.GetBoolean(8),
        Enum.Parse<GmtReviewer>(r.GetString(9)),
        Enum.Parse<GmtStatus>(r.GetString(10)),
        r.GetFieldValue<DateTimeOffset>(11),
        r.IsDBNull(12) ? null : r.GetString(12));
}
