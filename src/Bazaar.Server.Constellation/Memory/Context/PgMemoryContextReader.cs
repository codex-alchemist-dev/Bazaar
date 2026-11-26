using Bazaar.Server.Memory.Retrieval;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory.Context;

/// <summary>The text of one candidate doc for packing: its path, memory note id and tier when it is a memory note, and its body.</summary>
public sealed record MemoryContextDocText(Guid DocId, string VaultPath, string? NoteId, string? Trust, string Body);

/// <summary>The Vault index reads of the retrieval operation: lexical seeds and candidate bodies.</summary>
public interface IMemoryContextReader
{
    /// <summary>
    /// Full-text seeds: docs ranked by their best block's <c>ts_rank</c>, restricted by <paramref name="filter"/> in SQL before the
    /// limit, exactly like the vector query. The caller applies <c>IVaultAccess</c> to the result.
    /// </summary>
    Task<IReadOnlyList<MemoryVectorCandidate>> LexicalDocsAsync(string queryText, MemoryPathFilter filter, int limit, CancellationToken ct);

    /// <summary>The bodies of <paramref name="docIds"/> (blocks in order), with the active memory revision's note id and tier.</summary>
    Task<IReadOnlyList<MemoryContextDocText>> ReadAsync(IReadOnlyCollection<Guid> docIds, CancellationToken ct);
}

/// <summary>Postgres <see cref="IMemoryContextReader"/> over <c>vault_docs</c>, <c>vault_blocks</c> and the active memory revisions.</summary>
public sealed class PgMemoryContextReader(NpgsqlDataSource dataSource) : IMemoryContextReader
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string _lexicalSql = """
        SELECT ranked.doc_id, ranked.path, max(ranked.score)::float8 AS score
        FROM (
            SELECT b.doc_id, d.path, ts_rank(b.tsv, q.query) AS score
            FROM vault_blocks b
            JOIN vault_docs d ON d.doc_id = b.doc_id
            CROSS JOIN websearch_to_tsquery('english', $1::text) AS q(query)
            WHERE b.tsv @@ q.query
              AND ($2::uuid IS NULL OR d.vault_id = $2)
              AND (NOT $3::boolean OR NOT starts_with(d.path, 'agents/') OR ($4::text IS NOT NULL AND starts_with(d.path, $4::text)))
              AND ($5::text[] IS NULL OR EXISTS (SELECT 1 FROM unnest($5::text[]) AS p(prefix) WHERE starts_with(d.path, p.prefix)))
            ORDER BY score DESC, b.doc_id, b.ordinal
            LIMIT $6
        ) AS ranked
        GROUP BY ranked.doc_id, ranked.path
        ORDER BY score DESC, ranked.doc_id
        """;

    private const string _textSql = """
        SELECT d.doc_id, d.path, r.note_id, r.trust, string_agg(b.body, E'\n\n' ORDER BY b.ordinal)
        FROM vault_docs d
        JOIN vault_blocks b ON b.doc_id = d.doc_id
        LEFT JOIN constellation_memory_revision r ON r.doc_id = d.doc_id AND r.is_active
        WHERE d.doc_id = ANY($1)
        GROUP BY d.doc_id, d.path, r.note_id, r.trust
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryVectorCandidate>> LexicalDocsAsync(string queryText, MemoryPathFilter filter, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var cmd = _dataSource.CreateCommand(_lexicalSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = queryText });
        PgMemoryVectorQuery.AddFilterParameters(cmd, filter);
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

        var rows = new List<MemoryVectorCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MemoryVectorCandidate(reader.GetGuid(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryContextDocText>> ReadAsync(IReadOnlyCollection<Guid> docIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(docIds);
        if (docIds.Count == 0)
        {
            return [];
        }

        await using var cmd = _dataSource.CreateCommand(_textSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = docIds.Distinct().ToArray() });

        var rows = new List<MemoryContextDocText>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MemoryContextDocText(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }
}

/// <summary>Turns lexical doc candidates into ranked seeds.</summary>
internal static class MemoryContextSeeds
{
    public static RankedHit[] ToRanked(IEnumerable<MemoryVectorCandidate> candidates) =>
        [.. candidates.Select(c => new RankedHit(c.DocId.ToString("D"), c.Score))];
}
