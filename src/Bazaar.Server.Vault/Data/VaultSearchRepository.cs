using Bazaar.Server.Vault.Search;
using Npgsql;

namespace Bazaar.Server.Vault.Data;

/// <summary>One block returned by a candidate query, with the card fields it needs.</summary>
public sealed record VaultBlockRow(
    Guid DocId,
    int Ordinal,
    string Path,
    string Title,
    string Type,
    string? Project,
    IReadOnlyList<string> HeadingPath,
    string Body,
    double Score);

/// <summary>
/// Read side of the Vault index for search (doc 18, "Search" and "Access"): lexical and vector candidates, one-hop link
/// neighbours, and body fetch by id. Every query applies the caller's access rule in SQL, so private documents are
/// ranked out rather than filtered afterwards.
/// </summary>
public sealed class VaultSearchRepository(NpgsqlDataSource dataSource)
{
    // Access, shared by every query. $1 is the caller's IsOwner and $2 the caller's agent owner (or null).
    private const string _accessClause = "($1::boolean OR d.owner IS NULL OR d.owner = $2::text)";

    private const string _filterClause = """
        AND ($3::text IS NULL OR d.project = $3::text)
        AND ($4::text IS NULL OR d.type = $4::text)
        """;

    /// <summary>Full-text candidates, best first. Uses the <c>tsv</c> column written by the indexer.</summary>
    public async Task<IReadOnlyList<VaultBlockRow>> LexicalCandidatesAsync(
        Guid vaultId, string text, VaultCaller caller, string? project, string? type, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT b.doc_id, b.ordinal, d.path, d.title, d.type, d.project, b.heading_path, b.body,
                   ts_rank(b.tsv, q.query)::float8 AS score
            FROM vault_blocks b
            JOIN vault_docs d ON d.doc_id = b.doc_id
            CROSS JOIN websearch_to_tsquery('english', $5::text) AS q(query)
            WHERE d.vault_id = $6 AND b.tsv @@ q.query AND {_accessClause} {_filterClause}
            ORDER BY score DESC, b.doc_id, b.ordinal
            LIMIT $7
            """);
        AddCommonParameters(cmd, caller, project, type);
        cmd.Parameters.AddWithValue(text);
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(limit);
        return await ReadBlockRowsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vector candidates under one embedding profile, nearest first (cosine distance on <c>halfvec</c>).</summary>
    public async Task<IReadOnlyList<VaultBlockRow>> VectorCandidatesAsync(
        Guid vaultId, Guid profileId, float[] vector, VaultCaller caller, string? project, string? type, int limit,
        CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT b.doc_id, b.ordinal, d.path, d.title, d.type, d.project, b.heading_path, b.body,
                   (1 - (e.embedding <=> $5::text::halfvec))::float8 AS score
            FROM vault_block_embedding e
            JOIN vault_blocks b ON b.doc_id = e.doc_id AND b.ordinal = e.ordinal
            JOIN vault_docs d ON d.doc_id = b.doc_id
            WHERE e.profile_id = $6 AND d.vault_id = $7 AND {_accessClause} {_filterClause}
            ORDER BY e.embedding <=> $5::text::halfvec, b.doc_id, b.ordinal
            LIMIT $8
            """);
        AddCommonParameters(cmd, caller, project, type);
        cmd.Parameters.AddWithValue(VaultEmbeddingRepository.ToVectorLiteral(vector));
        cmd.Parameters.AddWithValue(profileId);
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(limit);
        return await ReadBlockRowsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The first block of each document one wiki-link away from the seeds, in either direction (depth 1). Targets the
    /// caller cannot read are left out, so a link into private memory does not reveal that the document exists.
    /// </summary>
    public async Task<IReadOnlyList<VaultBlockRow>> NeighbourCardsAsync(
        Guid vaultId, IReadOnlyCollection<Guid> seedDocIds, VaultCaller caller, int limit, CancellationToken cancellationToken)
    {
        if (seedDocIds.Count == 0)
        {
            return [];
        }

        await using var cmd = dataSource.CreateCommand($"""
            WITH neighbours AS (
                SELECT l.target_doc AS doc_id FROM vault_links l
                WHERE l.src_doc = ANY($4::uuid[]) AND l.target_doc IS NOT NULL
                UNION
                SELECT l.src_doc FROM vault_links l
                WHERE l.target_doc = ANY($4::uuid[])
            )
            SELECT first_block.doc_id, first_block.ordinal, d.path, d.title, d.type, d.project, first_block.heading_path,
                   first_block.body, 0::float8 AS score
            FROM neighbours n
            JOIN vault_docs d ON d.doc_id = n.doc_id
            CROSS JOIN LATERAL (
                SELECT b.doc_id, b.ordinal, b.heading_path, b.body FROM vault_blocks b
                WHERE b.doc_id = d.doc_id ORDER BY b.ordinal LIMIT 1
            ) first_block
            WHERE d.vault_id = $3 AND NOT (d.doc_id = ANY($4::uuid[])) AND {_accessClause}
            ORDER BY d.path
            LIMIT $5
            """);
        AddAccessParameters(cmd, caller);
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(seedDocIds.ToArray());
        cmd.Parameters.AddWithValue(limit);
        return await ReadBlockRowsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The body of one block, if the caller can read its document. Null otherwise (absent or private).</summary>
    public async Task<string?> GetBodyAsync(Guid vaultId, Guid docId, int ordinal, VaultCaller caller, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT b.body FROM vault_blocks b JOIN vault_docs d ON d.doc_id = b.doc_id
            WHERE d.vault_id = $3 AND b.doc_id = $4 AND b.ordinal = $5 AND {_accessClause}
            """);
        AddAccessParameters(cmd, caller);
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(docId);
        cmd.Parameters.AddWithValue(ordinal);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>The commit the index has caught up to, or null before the first index run (doc 18, "Behaviour").</summary>
    public async Task<string?> GetIndexedCommitAsync(Guid vaultId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT indexed_commit FROM vault_state WHERE vault_id = $1");
        cmd.Parameters.AddWithValue(vaultId);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static void AddAccessParameters(NpgsqlCommand cmd, VaultCaller caller)
    {
        cmd.Parameters.AddWithValue(caller.IsOwner);
        cmd.Parameters.AddWithValue((object?)caller.AgentOwner ?? DBNull.Value);
    }

    private static void AddCommonParameters(NpgsqlCommand cmd, VaultCaller caller, string? project, string? type)
    {
        AddAccessParameters(cmd, caller);
        cmd.Parameters.AddWithValue((object?)project ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)type ?? DBNull.Value);
    }

    private static async Task<IReadOnlyList<VaultBlockRow>> ReadBlockRowsAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<VaultBlockRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new VaultBlockRow(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<string[]>(6),
                reader.GetString(7),
                reader.GetDouble(8)));
        }

        return rows;
    }
}
