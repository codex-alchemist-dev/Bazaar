using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Postgres <see cref="IMemoryGraphStore"/> over <c>constellation_memory_revision</c> and <c>constellation_memory_edge</c>
/// (<c>constellation/0001</c>). Raw Npgsql with SQL-first commands; values always travel as parameters.
/// </summary>
public sealed class PgMemoryGraphStore(NpgsqlDataSource dataSource) : IMemoryGraphStore
{
    private const string _uniqueViolation = "23505";
    private const string _revisionPrimaryKey = "constellation_memory_revision_pkey";

    private const string _insertRevisionSql = """
        INSERT INTO constellation_memory_revision
            (note_id, version, scope_agent_id, vault_path, title, kind, status, trust, is_active)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, false)
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
        SELECT note_id, version, scope_agent_id, vault_path, title, kind, status, trust
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
        SELECT note_id, version, scope_agent_id, vault_path, title, kind, status, trust
        FROM constellation_memory_revision
        WHERE note_id = $1
        ORDER BY version DESC
        """;

    private const string _listEdgesSql = """
        SELECT note_id, version, target_id, kind, weight
        FROM constellation_memory_edge
        WHERE note_id = $1
        """;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The (note, version) pair is already stored. Nothing is changed.</exception>
    public async Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateRevision(revision);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    }

    /// <inheritdoc />
    public async Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

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

    /// <summary>Reads revision rows, then their edges in one more query, and attaches the edges in a stable order.</summary>
    private async Task<IReadOnlyList<MemoryGraphRevision>> ReadRevisionsAsync(
        string revisionSql,
        string edgeSql,
        Action<NpgsqlCommand> bind,
        CancellationToken ct)
    {
        var revisions = new List<(string NoteId, long Version, string ScopeAgentId, string VaultPath, string Title, string Kind, string Status, string Trust)>();
        await using (var cmd = dataSource.CreateCommand(revisionSql))
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
                    reader.GetString(7)));
            }
        }

        if (revisions.Count == 0)
            return [];

        var edges = new Dictionary<(string NoteId, long Version), List<MemoryGraphEdge>>();
        await using (var cmd = dataSource.CreateCommand(edgeSql))
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

        var result = new List<MemoryGraphRevision>(revisions.Count);
        foreach (var row in revisions)
        {
            var rowEdges = edges.TryGetValue((row.NoteId, row.Version), out var found) ? found : [];
            var ordered = rowEdges
                .OrderBy(e => e.Kind.ToString(), StringComparer.Ordinal)
                .ThenBy(e => e.TargetId, StringComparer.Ordinal)
                .ToList();
            result.Add(new MemoryGraphRevision(
                row.NoteId, row.Version, row.ScopeAgentId, row.VaultPath, row.Title, row.Kind, row.Status, row.Trust, ordered));
        }

        return result;
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
