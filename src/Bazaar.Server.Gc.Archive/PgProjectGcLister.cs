using Bazaar.Archive.Gc;
using Npgsql;

namespace Bazaar.Server.Gc.Archive;

/// <summary>
/// The GCs of a project: the project GC (its id is the project id) and every GC below it, in a stable order (project GC first,
/// then by id). Thinking GCs are left out because their text is never archived (doc 25 floors); archived GCs are kept in so
/// a second run reproduces the same bundle.
/// </summary>
public sealed class PgProjectGcLister(NpgsqlDataSource dataSource) : IProjectGcLister
{
    private const string _sql = """
        WITH RECURSIVE tree AS (
            SELECT gc_id, type_key, 0 AS depth FROM gc WHERE gc_id = $1
            UNION ALL
            SELECT c.gc_id, c.type_key, t.depth + 1 FROM gc c JOIN tree t ON c.parent_gc_id = t.gc_id
        )
        SELECT gc_id FROM tree WHERE type_key <> 'thinking' ORDER BY depth, gc_id
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListGcIdsAsync(string projectId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(projectId, "D", out var root))
            throw new ArgumentException("A project id is the project GC's id.", nameof(projectId));

        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = root });
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetGuid(0));
        return ids;
    }
}
