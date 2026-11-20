using System.Data;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Postgres <see cref="IMemoryGraphStore"/> over <c>constellation_memory_revision</c> and <c>constellation_memory_edge</c>
/// (<c>constellation/0001</c>, <c>constellation/0002</c>). Raw Npgsql with SQL-first commands; values always travel as parameters.
/// </summary>
/// <remarks>
/// Each write maps the note to its Vault doc node (docs/design/constellation-memory-graph.md, "Convergence design"): the doc id
/// on the revision wins, otherwise it is resolved from the Vault index by path (<see cref="MemoryDocIdResolution"/>). Once a note
/// has a doc id, every earlier revision of it that had none gets the same one, so history is keyed by the doc.
/// </remarks>
/// <param name="dataSource">The shared data source.</param>
/// <param name="vaultId">The Vault whose index maps paths to doc ids (<c>Constellation:VaultId</c>), or null for any Vault.</param>
public sealed class PgMemoryGraphStore(NpgsqlDataSource dataSource, Guid? vaultId = null) : IMemoryGraphStore
{
    private const string _uniqueViolation = "23505";
    private const string _revisionPrimaryKey = "constellation_memory_revision_pkey";
    private const string _activeDocKey = "constellation_memory_revision_active_doc_key";

    /// <summary>Takes a transaction-scoped advisory lock per note id, so writers for one note run one at a time.</summary>
    private const string _lockNoteSql = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";

    private const string _ownersSql = """
        SELECT scope_agent_id, vault_path, doc_id
        FROM constellation_memory_revision
        WHERE note_id = $1
        """;

    private const string _backfillDocSql = """
        UPDATE constellation_memory_revision
        SET doc_id = $2
        WHERE note_id = $1 AND doc_id IS NULL
        """;

    private const string _activeVersionSql = """
        SELECT version
        FROM constellation_memory_revision
        WHERE note_id = $1 AND is_active
        """;

    private const string _insertRevisionSql = """
        INSERT INTO constellation_memory_revision
            (note_id, version, scope_agent_id, vault_path, title, kind, status, trust, is_active, doc_id)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, false, $9)
        """;

    private const string _deactivateOthersSql = """
        UPDATE constellation_memory_revision
        SET is_active = false
        WHERE note_id = $1 AND version <> $2 AND is_active
        """;

    private const string _activateSql = """
        UPDATE constellation_memory_revision
        SET is_active = true
        WHERE note_id = $1 AND version = $2
        """;

    private const string _insertEdgeSql = """
        INSERT INTO constellation_memory_edge (note_id, version, target_id, kind, weight)
        VALUES ($1, $2, $3, $4, $5)
        """;

    private const string _existsSql = "SELECT count(*) FROM constellation_memory_revision WHERE note_id = $1 AND version = $2";

    private const string _activeRevisionsSql = """
        SELECT note_id, version, scope_agent_id, vault_path, title, kind, status, trust, doc_id
        FROM constellation_memory_revision
        WHERE is_active AND note_id = ANY($1)
        ORDER BY note_id
        """;

    private const string _activeEdgesSql = """
        SELECT e.note_id, e.version, e.target_id, e.kind, e.weight
        FROM constellation_memory_edge e
        JOIN constellation_memory_revision r ON r.note_id = e.note_id AND r.version = e.version
        WHERE r.is_active AND r.note_id = ANY($1)
        """;

    private const string _listRevisionsSql = """
        SELECT note_id, version, scope_agent_id, vault_path, title, kind, status, trust, doc_id
        FROM constellation_memory_revision
        WHERE note_id = $1
        ORDER BY version DESC
        """;

    private const string _listRevisionsByDocSql = """
        SELECT note_id, version, scope_agent_id, vault_path, title, kind, status, trust, doc_id
        FROM constellation_memory_revision
        WHERE doc_id = $1
        ORDER BY version DESC, note_id
        """;

    private const string _listEdgesByDocSql = """
        SELECT e.note_id, e.version, e.target_id, e.kind, e.weight
        FROM constellation_memory_edge e
        JOIN constellation_memory_revision r ON r.note_id = e.note_id AND r.version = e.version
        WHERE r.doc_id = $1
        """;

    private const string _listEdgesSql = """
        SELECT note_id, version, target_id, kind, weight
        FROM constellation_memory_edge
        WHERE note_id = $1
        """;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The (note, version) pair is already stored, the note belongs to another agent or vault path, or the revision is not
    /// newer than the active one. Nothing is changed.
    /// </exception>
    public async Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateRevision(revision);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The lock is the first statement, so writers for one note are serialized before they read or write.
        await LockNoteAsync(conn, tx, revision.NoteId, ct).ConfigureAwait(false);

        // Ownership is checked before any write: a note id belongs to one agent and one vault path for its whole history.
        Guid? storedDocId = null;
        await using (var owners = Command(conn, tx, _ownersSql))
        {
            AddParameter(owners, revision.NoteId);
            await using var reader = await owners.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != revision.ScopeAgentId || reader.GetString(1) != revision.VaultPath)
                {
                    throw new InvalidOperationException(
                        $"Memory note {revision.NoteId} is already stored for another agent or vault path. Nothing was changed.");
                }

                if (!reader.IsDBNull(2))
                    storedDocId = reader.GetGuid(2);
            }
        }

        // The doc node: a doc id set once for the note never changes (constellation/0002 enforces it too).
        var docId = revision.DocId
            ?? storedDocId
            ?? await MemoryDocIdResolution.ResolveAsync(conn, tx, revision.NoteId, revision.VaultPath, vaultId, ct).ConfigureAwait(false);
        if (storedDocId is { } known && docId != known)
        {
            throw new InvalidOperationException(
                $"Memory note {revision.NoteId} is mapped to Vault doc {known}, not {docId}. Nothing was changed.");
        }

        try
        {
            // Insert inactive first, so the partial unique index never sees two active rows for the note.
            await using (var insert = Command(conn, tx, _insertRevisionSql))
            {
                AddParameter(insert, revision.NoteId);
                AddParameter(insert, revision.Version);
                AddParameter(insert, revision.ScopeAgentId);
                AddParameter(insert, revision.VaultPath);
                AddParameter(insert, revision.Title);
                AddParameter(insert, revision.Kind);
                AddParameter(insert, revision.Status);
                AddParameter(insert, revision.Trust);
                insert.Parameters.Add(new NpgsqlParameter { Value = (object?)docId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Earlier revisions stored before the Vault index knew the file get the same doc id now.
            if (docId is { } mapped)
            {
                await using var backfill = Command(conn, tx, _backfillDocSql);
                AddParameter(backfill, revision.NoteId);
                AddParameter(backfill, mapped);
                await backfill.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // The new row is inactive, so this reads the previously active version (if any). A stale replay is refused here.
            var activeVersion = await ActiveVersionAsync(conn, tx, revision.NoteId, ct).ConfigureAwait(false);
            if (activeVersion is { } active && revision.Version <= active)
            {
                throw new InvalidOperationException("store must be newer than the active revision");
            }

            await using (var deactivate = Command(conn, tx, _deactivateOthersSql))
            {
                AddParameter(deactivate, revision.NoteId);
                AddParameter(deactivate, revision.Version);
                await deactivate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var activate = Command(conn, tx, _activateSql))
            {
                AddParameter(activate, revision.NoteId);
                AddParameter(activate, revision.Version);
                await activate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var edge in revision.Edges)
            {
                await using var insertEdge = Command(conn, tx, _insertEdgeSql);
                AddParameter(insertEdge, revision.NoteId);
                AddParameter(insertEdge, revision.Version);
                AddParameter(insertEdge, edge.TargetId);
                AddParameter(insertEdge, edge.Kind.ToString());
                AddParameter(insertEdge, edge.Weight);
                await insertEdge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == _uniqueViolation && ex.ConstraintName == _revisionPrimaryKey)
        {
            // The transaction is disposed without a commit, so the rollback leaves the stored revisions as they were.
            throw new InvalidOperationException(
                $"Memory revision {revision.NoteId} version {revision.Version} is already stored. Revisions are append-only.", ex);
        }
        catch (PostgresException ex) when (ex.SqlState == _uniqueViolation && ex.ConstraintName == _activeDocKey)
        {
            throw new InvalidOperationException(
                $"Memory revision {revision.NoteId} version {revision.Version} could not be stored: Vault doc {docId} already has an active revision of another note. Nothing was changed.",
                ex);
        }
        catch (PostgresException ex) when (ex.SqlState == _uniqueViolation)
        {
            // Not reachable while the note lock holds. Mapped anyway, so no caller sees a raw database error here.
            throw new InvalidOperationException(
                $"Memory revision {revision.NoteId} could not be stored: a concurrent write conflicted. Nothing was changed.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await LockNoteAsync(conn, tx, noteId, ct).ConfigureAwait(false);

        // Check first, so an unknown version changes nothing (not even the currently active row).
        await using (var exists = Command(conn, tx, _existsSql))
        {
            AddParameter(exists, noteId);
            AddParameter(exists, version);
            if ((long)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0)
                return false;
        }

        await using (var deactivate = Command(conn, tx, _deactivateOthersSql))
        {
            AddParameter(deactivate, noteId);
            AddParameter(deactivate, version);
            await deactivate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var activate = Command(conn, tx, _activateSql))
        {
            AddParameter(activate, noteId);
            AddParameter(activate, version);
            await activate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        if (noteIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Note ids must not be null, empty or white space.", nameof(noteIds));

        var ids = noteIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return [];

        return await ReadRevisionsAsync(
            _activeRevisionsSql,
            _activeEdgesSql,
            cmd => AddParameter(cmd, ids),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);

        return await ReadRevisionsAsync(
            _listRevisionsSql,
            _listEdgesSql,
            cmd => AddParameter(cmd, noteId),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsByDocAsync(Guid docId, CancellationToken ct) =>
        await ReadRevisionsAsync(
            _listRevisionsByDocSql,
            _listEdgesByDocSql,
            cmd => AddParameter(cmd, docId),
            ct).ConfigureAwait(false);

    /// <summary>
    /// Reads revision rows, then their edges, in one repeatable-read transaction, so the edges always match the revisions
    /// that were read. Attaches the edges in a stable order.
    /// </summary>
    private async Task<IReadOnlyList<MemoryGraphRevision>> ReadRevisionsAsync(
        string revisionSql,
        string edgeSql,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        var revisions = new List<(string NoteId, long Version, string ScopeAgentId, string VaultPath, string Title, string Kind, string Status, string Trust, Guid? DocId)>();
        await using (var cmd = Command(conn, tx, revisionSql))
        {
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                revisions.Add((
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8)));
            }
        }

        if (revisions.Count == 0)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return [];
        }

        var edges = new Dictionary<(string NoteId, long Version), List<MemoryGraphEdge>>();
        await using (var cmd = Command(conn, tx, edgeSql))
        {
            bind(cmd);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetInt64(1));
                if (!edges.TryGetValue(key, out var list))
                    edges[key] = list = [];
                list.Add(new MemoryGraphEdge(
                    reader.GetString(2),
                    Enum.Parse<MemoryEdgeKind>(reader.GetString(3), ignoreCase: false),
                    reader.GetDouble(4)));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var result = new List<MemoryGraphRevision>(revisions.Count);
        foreach (var row in revisions)
        {
            var rowEdges = edges.TryGetValue((row.NoteId, row.Version), out var found) ? found : [];
            var ordered = rowEdges
                .OrderBy(e => e.Kind.ToString(), StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal)
                .ToList();
            result.Add(new MemoryGraphRevision(
                row.NoteId, row.Version, row.ScopeAgentId, row.VaultPath, row.Title, row.Kind, row.Status, row.Trust, ordered, row.DocId));
        }

        return result;
    }

    private static async Task LockNoteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string noteId, CancellationToken ct)
    {
        await using var lockCmd = Command(conn, tx, _lockNoteSql);
        AddParameter(lockCmd, noteId);
        await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The version of the active revision for a note, or null when the note has none.</summary>
    private static async Task<long?> ActiveVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string noteId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, _activeVersionSql);
        AddParameter(cmd, noteId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetInt64(0) : null;
    }

    private static void ValidateRevision(MemoryGraphRevision revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.NoteId, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.ScopeAgentId, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.VaultPath, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Kind, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Status, nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Trust, nameof(revision));
        ArgumentNullException.ThrowIfNull(revision.Title, nameof(revision));
        ArgumentNullException.ThrowIfNull(revision.Edges, nameof(revision));
        if (revision.DocId == Guid.Empty)
            throw new ArgumentException("A doc id, when given, must not be empty.", nameof(revision));

        var seen = new HashSet<(string, MemoryEdgeKind)>();
        foreach (var edge in revision.Edges)
        {
            ArgumentNullException.ThrowIfNull(edge, nameof(revision));
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.TargetId, nameof(revision));
            if (!Enum.IsDefined(edge.Kind))
                throw new ArgumentException($"Unknown edge kind {(int)edge.Kind}.", nameof(revision));
            if (double.IsNaN(edge.Weight) || edge.Weight < 0 || edge.Weight > 1)
                throw new ArgumentException($"Edge weight {edge.Weight} is outside [0, 1].", nameof(revision));
            if (!seen.Add((edge.TargetId, edge.Kind)))
                throw new ArgumentException($"Duplicate edge {edge.Kind} to '{edge.TargetId}'.", nameof(revision));
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void AddParameter<T>(NpgsqlCommand cmd, T value) =>
        cmd.Parameters.Add(new NpgsqlParameter<T> { TypedValue = value });
}
