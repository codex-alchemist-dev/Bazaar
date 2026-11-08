using Bazaar.Server.Vault.Links;
using Npgsql;

namespace Bazaar.Server.Vault.Data;

/// <summary>
/// Document, block and link rows of the Vault index (<c>vault_docs</c>, <c>vault_blocks</c>, <c>vault_links</c>).
/// <see cref="ReplaceDocumentAsync"/> is the single write path: it keeps embeddings whose block text did not change
/// and rewrites the rest in one transaction. Links are supplied already resolved by the caller.
/// </summary>
public sealed class VaultIndexRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Inserts or replaces a document by <see cref="VaultDocRecord.DocId"/>. A changed path is a rename and keeps the id.
    /// Embedding rows survive only when their ordinal still holds a block with the same hash.
    /// </summary>
    public async Task ReplaceDocumentAsync(
        VaultDocRecord doc,
        IReadOnlyList<VaultBlockRecord> blocks,
        IReadOnlyList<VaultLinkRecord> links,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(links);
        if (links.Any(link => link.SrcDoc != doc.DocId))
        {
            throw new ArgumentException("Every link must belong to the document being replaced.", nameof(links));
        }

        var ordinals = blocks.Select(block => block.Ordinal).ToArray();
        var hashes = blocks.Select(block => block.TextHash).ToArray();

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO vault_docs (doc_id, vault_id, path, title, type, project, area, owner, frontmatter, head_commit, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb, $10::char(40), $11)
            ON CONFLICT (doc_id) DO UPDATE SET vault_id = EXCLUDED.vault_id, path = EXCLUDED.path, title = EXCLUDED.title,
                type = EXCLUDED.type, project = EXCLUDED.project, area = EXCLUDED.area, owner = EXCLUDED.owner,
                frontmatter = EXCLUDED.frontmatter, head_commit = EXCLUDED.head_commit, updated_at = EXCLUDED.updated_at
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue(doc.DocId);
            cmd.Parameters.AddWithValue(doc.VaultId);
            cmd.Parameters.AddWithValue(doc.Path);
            cmd.Parameters.AddWithValue(doc.Title);
            cmd.Parameters.AddWithValue(doc.Type);
            cmd.Parameters.AddWithValue(doc.Project ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(doc.Area);
            cmd.Parameters.AddWithValue(doc.Owner ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(doc.FrontmatterJson);
            cmd.Parameters.AddWithValue(doc.HeadCommit ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(doc.UpdatedAt);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Links first: they reference blocks, and the ones that point at this document's blocks are rebuilt below.
        await ExecuteAsync(conn, tx, "DELETE FROM vault_links WHERE src_doc = $1", [doc.DocId], cancellationToken);

        // Embeddings whose block is gone or whose text changed are stale for every profile.
        await ExecuteAsync(conn, tx, """
            DELETE FROM vault_block_embedding e
            WHERE e.doc_id = $1 AND NOT EXISTS (
                SELECT 1 FROM unnest($2::int[], $3::bytea[]) AS b(ordinal, text_hash)
                WHERE b.ordinal = e.ordinal AND b.text_hash = e.text_hash)
            """, [doc.DocId, ordinals, hashes], cancellationToken);

        // Clear block ids first so a block that moves to another ordinal does not collide with the unique constraint.
        await ExecuteAsync(conn, tx, "UPDATE vault_blocks SET block_id = NULL WHERE doc_id = $1", [doc.DocId], cancellationToken);

        if (blocks.Count > 0)
        {
            await using var batch = new NpgsqlBatch(conn) { Transaction = tx };
            foreach (var block in blocks)
            {
                var command = new NpgsqlBatchCommand("""
                    INSERT INTO vault_blocks (doc_id, ordinal, block_id, heading_path, start_line, end_line, text_hash, body)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                    ON CONFLICT (doc_id, ordinal) DO UPDATE SET block_id = EXCLUDED.block_id, heading_path = EXCLUDED.heading_path,
                        start_line = EXCLUDED.start_line, end_line = EXCLUDED.end_line, text_hash = EXCLUDED.text_hash, body = EXCLUDED.body
                    """);
                command.Parameters.AddWithValue(doc.DocId);
                command.Parameters.AddWithValue(block.Ordinal);
                command.Parameters.AddWithValue(block.BlockId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue(block.HeadingPath.ToArray());
                command.Parameters.AddWithValue(block.StartLine);
                command.Parameters.AddWithValue(block.EndLine);
                command.Parameters.AddWithValue(block.TextHash);
                command.Parameters.AddWithValue(block.Body);
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(conn, tx, "DELETE FROM vault_blocks WHERE doc_id = $1 AND NOT (ordinal = ANY($2::int[]))",
            [doc.DocId, ordinals], cancellationToken);

        if (links.Count > 0)
        {
            await using var batch = new NpgsqlBatch(conn) { Transaction = tx };
            foreach (var link in links)
            {
                var command = new NpgsqlBatchCommand("""
                    INSERT INTO vault_links (src_doc, src_ordinal, link_ord, kind, target_raw, target_doc, target_anchor, alias)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                    """);
                command.Parameters.AddWithValue(link.SrcDoc);
                command.Parameters.AddWithValue(link.SrcOrdinal);
                command.Parameters.AddWithValue(link.LinkOrd);
                command.Parameters.AddWithValue(link.Kind);
                command.Parameters.AddWithValue(link.TargetRaw);
                command.Parameters.AddWithValue(link.TargetDoc ?? (object)DBNull.Value);
                command.Parameters.AddWithValue(link.TargetAnchor ?? (object)DBNull.Value);
                command.Parameters.AddWithValue(link.Alias ?? (object)DBNull.Value);
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a document and its blocks and embeddings. Links into it stay as unresolved links (target null, raw text kept),
    /// so they re-resolve if a document with the same path comes back. Returns false if the document was not indexed.
    /// </summary>
    public async Task<bool> DeleteDocumentAsync(Guid docId, CancellationToken cancellationToken = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(conn, tx, "UPDATE vault_links SET target_doc = NULL WHERE target_doc = $1", [docId], cancellationToken);
        await ExecuteAsync(conn, tx, "DELETE FROM vault_links WHERE src_doc = $1", [docId], cancellationToken);
        await ExecuteAsync(conn, tx, "DELETE FROM vault_block_embedding WHERE doc_id = $1", [docId], cancellationToken);
        await ExecuteAsync(conn, tx, "DELETE FROM vault_blocks WHERE doc_id = $1", [docId], cancellationToken);
        var deleted = await ExecuteAsync(conn, tx, "DELETE FROM vault_docs WHERE doc_id = $1", [docId], cancellationToken);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }

    /// <summary>
    /// Every indexed document of a Vault as a resolver entry: path, title, aliases, project, heading slugs and block ids.
    /// The indexer uses these for unchanged documents, so a catch-up does not re-parse them.
    /// </summary>
    public async Task<IReadOnlyList<VaultIndexEntry>> ListEntriesAsync(Guid vaultId, CancellationToken cancellationToken = default)
    {
        var docs = new List<(Guid DocId, string Path, string Title, string? Project, string FrontmatterJson)>();
        await using (var cmd = dataSource.CreateCommand("""
            SELECT doc_id, path, title, project, frontmatter::text FROM vault_docs WHERE vault_id = $1 ORDER BY path
            """))
        {
            cmd.Parameters.AddWithValue(vaultId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                docs.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }
        }

        var headingSlugs = new Dictionary<Guid, HashSet<string>>();
        var blockIds = new Dictionary<Guid, List<string>>();
        await using (var cmd = dataSource.CreateCommand("""
            SELECT b.doc_id, b.block_id, b.heading_path
            FROM vault_blocks b JOIN vault_docs d ON d.doc_id = b.doc_id
            WHERE d.vault_id = $1
            """))
        {
            cmd.Parameters.AddWithValue(vaultId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var docId = reader.GetGuid(0);
                if (!reader.IsDBNull(1))
                {
                    if (!blockIds.TryGetValue(docId, out var ids))
                    {
                        blockIds[docId] = ids = [];
                    }

                    ids.Add(reader.GetString(1));
                }

                var path = reader.GetFieldValue<string[]>(2);
                if (path.Length > 0)
                {
                    if (!headingSlugs.TryGetValue(docId, out var slugs))
                    {
                        headingSlugs[docId] = slugs = new HashSet<string>(StringComparer.Ordinal);
                    }

                    slugs.Add(VaultSlug.Heading(path[^1]));
                }
            }
        }

        return docs.Select(doc => new VaultIndexEntry(
            doc.DocId,
            doc.Path,
            doc.Title,
            AliasesOf(doc.FrontmatterJson),
            doc.Project,
            headingSlugs.TryGetValue(doc.DocId, out var slugs) ? slugs.ToArray() : [],
            blockIds.TryGetValue(doc.DocId, out var ids) ? ids.ToArray() : [])).ToList();
    }

    /// <summary>
    /// Documents with at least one link that could resolve differently after a change: a link into one of
    /// <paramref name="docIds"/>, an unresolved link, or a link whose text matches one of <paramref name="names"/>
    /// (lower-case path without <c>.md</c>, title or alias).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> FindLinkingDocumentsAsync(
        Guid vaultId,
        IReadOnlyCollection<Guid> docIds,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT DISTINCT l.src_doc
            FROM vault_links l JOIN vault_docs d ON d.doc_id = l.src_doc
            WHERE d.vault_id = $1
              AND (l.target_doc IS NULL
                   OR l.target_doc = ANY($2::uuid[])
                   OR lower(regexp_replace(l.target_raw, '\.md$', '', 'i')) = ANY($3::text[]))
            """);
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(docIds.ToArray());
        cmd.Parameters.AddWithValue(names.ToArray());
        var result = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private static string[] AliasesOf(string frontmatterJson)
    {
        using var json = System.Text.Json.JsonDocument.Parse(frontmatterJson);
        if (!json.RootElement.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        return aliases.EnumerateArray().Select(alias => alias.GetString() ?? string.Empty).ToArray();
    }

    public async Task<VaultDocRecord?> GetDocumentAsync(Guid docId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_docSelect + " WHERE doc_id = $1");
        cmd.Parameters.AddWithValue(docId);
        return await ReadSingleDocAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultDocRecord?> GetDocumentByPathAsync(Guid vaultId, string path, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_docSelect + " WHERE vault_id = $1 AND path = $2");
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(path);
        return await ReadSingleDocAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Blocks of one document in document order.</summary>
    public async Task<IReadOnlyList<VaultBlockRecord>> GetBlocksAsync(Guid docId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT ordinal, block_id, heading_path, start_line, end_line, text_hash, body
            FROM vault_blocks WHERE doc_id = $1 ORDER BY ordinal
            """);
        cmd.Parameters.AddWithValue(docId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var blocks = new List<VaultBlockRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            blocks.Add(new VaultBlockRecord(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetFieldValue<string[]>(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetString(6)));
        }

        return blocks;
    }

    /// <summary>Links written by one document, in source order.</summary>
    public async Task<IReadOnlyList<VaultLinkRecord>> GetOutgoingLinksAsync(Guid docId, CancellationToken cancellationToken = default) =>
        await ReadLinksAsync("WHERE src_doc = $1", docId, cancellationToken).ConfigureAwait(false);

    /// <summary>Links that resolve to one document. Callers filter these by the caller's access before showing them.</summary>
    public async Task<IReadOnlyList<VaultLinkRecord>> GetBacklinksAsync(Guid docId, CancellationToken cancellationToken = default) =>
        await ReadLinksAsync("WHERE target_doc = $1", docId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Resolved links between documents of one Vault, as (source, target) pairs. Feeds the Constellation graph, which needs no
    /// anchors, aliases or access checks: those stay with the link reader.
    /// </summary>
    public async Task<IReadOnlyList<(Guid SrcDoc, Guid TargetDoc)>> ListResolvedLinksAsync(Guid vaultId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT l.src_doc, l.target_doc
            FROM vault_links l
            JOIN vault_docs d ON d.doc_id = l.src_doc
            WHERE d.vault_id = $1 AND l.target_doc IS NOT NULL
            ORDER BY l.src_doc, l.target_doc
            """);
        cmd.Parameters.AddWithValue(vaultId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(Guid SrcDoc, Guid TargetDoc)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        return rows;
    }

    /// <summary>Unresolved links in one Vault, so a later pass can try to resolve them.</summary>
    public async Task<IReadOnlyList<VaultLinkRecord>> GetUnresolvedLinksAsync(Guid vaultId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_linkSelect + """
             JOIN vault_docs d ON d.doc_id = l.src_doc
            WHERE d.vault_id = $1 AND l.target_doc IS NULL
            ORDER BY l.src_doc, l.src_ordinal, l.link_ord
            """);
        cmd.Parameters.AddWithValue(vaultId);
        return await ReadLinkRowsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    private const string _docSelect = """
        SELECT doc_id, vault_id, path, title, type, project, area, owner, frontmatter::text, head_commit, updated_at
        FROM vault_docs
        """;

    private const string _linkSelect = """
        SELECT l.src_doc, l.src_ordinal, l.link_ord, l.kind, l.target_raw, l.target_doc, l.target_anchor, l.alias
        FROM vault_links l
        """;

    private async Task<IReadOnlyList<VaultLinkRecord>> ReadLinksAsync(string where, Guid docId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(_linkSelect + " " + where
            + " ORDER BY l.src_doc, l.src_ordinal, l.link_ord");
        cmd.Parameters.AddWithValue(docId);
        return await ReadLinkRowsAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VaultLinkRecord>> ReadLinkRowsAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var links = new List<VaultLinkRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            links.Add(new VaultLinkRecord(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return links;
    }

    private static async Task<VaultDocRecord?> ReadSingleDocAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new VaultDocRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10));
    }

    /// <summary>Runs one statement in the transaction and returns the affected row count.</summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, object[] parameters, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter);
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
