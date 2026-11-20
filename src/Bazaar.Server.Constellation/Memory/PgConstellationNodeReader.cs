using System.Data;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Postgres <see cref="IConstellationNodeReader"/> (docs/design/constellation-memory-graph.md, "Convergence design"). Nodes are
/// <c>vault_docs</c> rows. A doc that is a memory note carries its active <c>constellation_memory_revision</c>. Edges are the
/// resolved <c>vault_links</c> of the doc (read live from the Vault index), plus the expanding edges of the active revision,
/// mapped to doc ids. All reads run in one repeatable-read transaction, so nodes, revisions and edges agree.
/// </summary>
/// <param name="dataSource">The shared data source.</param>
/// <param name="vaultId">The Vault whose docs form the graph (<c>Constellation:VaultId</c>), or null for any Vault.</param>
public sealed class PgConstellationNodeReader(NpgsqlDataSource dataSource, Guid? vaultId = null) : IConstellationNodeReader
{
    private const double _linkWeight = 1.0;

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string _docsSql = """
        SELECT doc_id, path, title, type, frontmatter->>'status'
        FROM vault_docs
        WHERE doc_id = ANY($1) AND ($2::uuid IS NULL OR vault_id = $2)
        """;

    private const string _revisionsSql = """
        SELECT note_id, version, scope_agent_id, vault_path, title, kind, status, trust, doc_id
        FROM constellation_memory_revision
        WHERE is_active AND doc_id = ANY($1)
        """;

    private const string _revisionEdgesSql = """
        SELECT e.note_id, e.target_id, e.kind, e.weight
        FROM constellation_memory_edge e
        JOIN constellation_memory_revision r ON r.note_id = e.note_id AND r.version = e.version
        WHERE r.is_active AND r.doc_id = ANY($1)
        """;

    private const string _linksSql = """
        SELECT src_doc, target_doc
        FROM vault_links
        WHERE src_doc = ANY($1) AND target_doc IS NOT NULL
        """;

    /// <summary>Note ids to doc ids: first through the revision table, then through the Vault front matter id.</summary>
    private const string _noteDocsSql = """
        SELECT note_id, doc_id
        FROM constellation_memory_revision
        WHERE note_id = ANY($1) AND doc_id IS NOT NULL
        UNION
        SELECT frontmatter->>'id', doc_id
        FROM vault_docs
        WHERE frontmatter->>'id' = ANY($1) AND ($2::uuid IS NULL OR vault_id = $2)
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConstellationGraphNode>> GetNodesAsync(IReadOnlyCollection<Guid> docIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(docIds);
        var ids = docIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        var docs = new List<(Guid DocId, string Path, string Title, string Type, string? Status)>();
        await using (var cmd = Command(conn, tx, _docsSql, ids))
        {
            cmd.Parameters.Add(MemoryDocIdResolution.VaultIdParameter(vaultId));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                docs.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        if (docs.Count == 0)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return [];
        }

        var found = docs.Select(d => d.DocId).ToArray();

        var revisions = new Dictionary<Guid, (string NoteId, long Version, string Scope, string Path, string Title, string Kind, string Status, string Trust)>();
        await using (var cmd = Command(conn, tx, _revisionsSql, found))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                revisions[reader.GetGuid(8)] = (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7));
            }
        }

        var revisionEdges = new Dictionary<string, List<MemoryGraphEdge>>(StringComparer.Ordinal);
        await using (var cmd = Command(conn, tx, _revisionEdgesSql, found))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var noteId = reader.GetString(0);
                if (!revisionEdges.TryGetValue(noteId, out var list))
                    revisionEdges[noteId] = list = [];
                list.Add(new MemoryGraphEdge(
                    reader.GetString(1),
                    Enum.Parse<MemoryEdgeKind>(reader.GetString(2), ignoreCase: false),
                    reader.GetDouble(3)));
            }
        }

        var links = new Dictionary<Guid, List<Guid>>();
        await using (var cmd = Command(conn, tx, _linksSql, found))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var src = reader.GetGuid(0);
                if (!links.TryGetValue(src, out var list))
                    links[src] = list = [];
                list.Add(reader.GetGuid(1));
            }
        }

        // Expanding revision edges that still name a note id (DerivedFrom, Supersedes, and Link edges stored before the
        // convergence) are mapped to doc ids. An id that maps to more than one doc is dropped, not guessed.
        var noteTargets = revisionEdges.Values
            .SelectMany(list => list)
            .Where(e => IsExpanding(e.Kind) && !Guid.TryParse(e.TargetId, out _))
            .Select(e => e.TargetId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var docByNote = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (noteTargets.Length > 0)
        {
            var seen = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
            await using var cmd = Command(conn, tx, _noteDocsSql, noteTargets);
            cmd.Parameters.Add(MemoryDocIdResolution.VaultIdParameter(vaultId));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var note = reader.GetString(0);
                if (!seen.TryGetValue(note, out var set))
                    seen[note] = set = [];
                set.Add(reader.GetGuid(1));
            }

            foreach (var (note, set) in seen)
            {
                if (set.Count == 1)
                    docByNote[note] = set.First();
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var nodes = new List<ConstellationGraphNode>(docs.Count);
        foreach (var doc in docs.OrderBy(d => d.DocId))
        {
            var edges = new Dictionary<(Guid Target, MemoryEdgeKind Kind), double>();
            if (links.TryGetValue(doc.DocId, out var targets))
            {
                foreach (var target in targets)
                    AddEdge(edges, doc.DocId, target, MemoryEdgeKind.Link, _linkWeight);
            }

            MemoryGraphRevision? memory = null;
            if (revisions.TryGetValue(doc.DocId, out var row))
            {
                var rowEdges = revisionEdges.TryGetValue(row.NoteId, out var list) ? list : [];
                memory = new MemoryGraphRevision(
                    row.NoteId, row.Version, row.Scope, row.Path, row.Title, row.Kind, row.Status, row.Trust,
                    [.. rowEdges.OrderBy(e => (int)e.Kind).ThenBy(e => e.TargetId, StringComparer.Ordinal)],
                    doc.DocId);

                foreach (var edge in rowEdges.Where(e => IsExpanding(e.Kind)))
                {
                    if (Guid.TryParse(edge.TargetId, out var targetDoc) || docByNote.TryGetValue(edge.TargetId, out targetDoc))
                        AddEdge(edges, doc.DocId, targetDoc, edge.Kind, edge.Weight);
                }
            }

            nodes.Add(new ConstellationGraphNode(
                doc.DocId,
                doc.Path,
                doc.Title,
                doc.Type,
                doc.Status,
                memory,
                [.. edges
                    .Select(pair => new ConstellationGraphEdge(pair.Key.Target, pair.Key.Kind, pair.Value))
                    .OrderBy(e => (int)e.Kind)
                    .ThenBy(e => e.TargetDocId)]));
        }

        return nodes;
    }

    internal static bool IsExpanding(MemoryEdgeKind kind) =>
        kind is MemoryEdgeKind.Link or MemoryEdgeKind.DerivedFrom or MemoryEdgeKind.Supersedes;

    /// <summary>Adds an edge; a self edge is dropped, and a repeated (target, kind) keeps the higher weight.</summary>
    private static void AddEdge(Dictionary<(Guid, MemoryEdgeKind), double> edges, Guid source, Guid target, MemoryEdgeKind kind, double weight)
    {
        if (target == source)
            return;
        edges[(target, kind)] = edges.TryGetValue((target, kind), out var existing) ? Math.Max(existing, weight) : weight;
    }

    private static NpgsqlCommand Command<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, T[] ids)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<T[]> { TypedValue = ids });
        return cmd;
    }
}
