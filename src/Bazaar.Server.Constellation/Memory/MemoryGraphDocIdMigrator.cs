using System.Data;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>One note the migrator maps (or would map) to a Vault doc.</summary>
/// <param name="NoteId">The memory note id.</param>
/// <param name="DocId">The Vault doc id it maps to.</param>
/// <param name="VaultPath">The note's vault path.</param>
/// <param name="Rows">Revision rows of the note that have no doc id yet and get this one.</param>
public sealed record MemoryDocIdMapping(string NoteId, Guid DocId, string VaultPath, int Rows);

/// <summary>A note the migrator leaves unmapped, with the reason code (<see cref="MemoryDocIdReasons"/>).</summary>
/// <param name="NoteId">The memory note id.</param>
/// <param name="VaultPath">The note's vault path (the lowest one when its revisions name several).</param>
/// <param name="Reason">The reason code.</param>
public sealed record MemoryDocIdIssue(string NoteId, string VaultPath, string Reason);

/// <summary>What a dry run would do, or what a run did.</summary>
/// <param name="DryRun">True for a dry run: nothing was changed.</param>
/// <param name="Mappings">Notes mapped (or to be mapped), ordered by note id.</param>
/// <param name="Unmapped">Notes with no Vault doc to map to; they stay unmapped.</param>
/// <param name="Conflicts">Notes whose mapping is not safe; they stay as they are.</param>
/// <param name="AlreadyMapped">Notes whose every revision already has its doc id.</param>
/// <param name="RowsUpdated">Revision rows changed. Always 0 for a dry run.</param>
public sealed record MemoryDocIdReport(
    bool DryRun,
    IReadOnlyList<MemoryDocIdMapping> Mappings,
    IReadOnlyList<MemoryDocIdIssue> Unmapped,
    IReadOnlyList<MemoryDocIdIssue> Conflicts,
    int AlreadyMapped,
    int RowsUpdated);

/// <summary>
/// Backfills <c>constellation_memory_revision.doc_id</c> from the Vault index (docs/design/constellation-memory-graph.md,
/// "Convergence design", item 5). <see cref="DryRunAsync"/> reports the plan in a read-only transaction and changes nothing.
/// <see cref="RunAsync"/> computes the same plan in one transaction under a table lock and sets doc ids only where they are
/// NULL. It never deletes or rewrites a revision, skips unmapped and conflicting notes, and is idempotent.
/// </summary>
/// <param name="dataSource">The shared data source.</param>
/// <param name="vaultId">The Vault whose index maps paths (<c>Constellation:VaultId</c>), or null for any Vault.</param>
public sealed class MemoryGraphDocIdMigrator(NpgsqlDataSource dataSource, Guid? vaultId = null)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string _notesSql = """
        SELECT note_id,
               array_agg(DISTINCT vault_path ORDER BY vault_path) AS paths,
               count(*) FILTER (WHERE doc_id IS NULL) AS unmapped_rows,
               coalesce(array_agg(DISTINCT doc_id) FILTER (WHERE doc_id IS NOT NULL), '{}') AS doc_ids
        FROM constellation_memory_revision
        GROUP BY note_id
        ORDER BY note_id
        """;

    private const string _candidatesSql = """
        SELECT d.path, d.doc_id, d.frontmatter->>'id'
        FROM vault_docs d
        WHERE d.path IN (SELECT DISTINCT vault_path FROM constellation_memory_revision)
          AND ($1::uuid IS NULL OR d.vault_id = $1)
        ORDER BY d.path, d.doc_id
        """;

    private const string _lockSql = "LOCK TABLE constellation_memory_revision IN SHARE ROW EXCLUSIVE MODE";

    private const string _updateSql = """
        UPDATE constellation_memory_revision
        SET doc_id = $2
        WHERE note_id = $1 AND doc_id IS NULL
        """;

    /// <summary>Reports the mappings, unmapped notes and conflicts. Runs read-only; nothing is changed.</summary>
    public async Task<MemoryDocIdReport> DryRunAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var plan = await PlanAsync(conn, tx, ct).ConfigureAwait(false);
        await tx.RollbackAsync(ct).ConfigureAwait(false);
        return plan with { DryRun = true };
    }

    /// <summary>Applies the plan in one transaction and reports what it did.</summary>
    public async Task<MemoryDocIdReport> RunAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        // Writers wait until the backfill commits, so the plan cannot go stale between reading and writing.
        await using (var lockCmd = new NpgsqlCommand(_lockSql, conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var plan = await PlanAsync(conn, tx, ct).ConfigureAwait(false);
        var updated = 0;
        foreach (var mapping in plan.Mappings)
        {
            await using var cmd = new NpgsqlCommand(_updateSql, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = mapping.NoteId });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = mapping.DocId });
            updated += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return plan with { DryRun = false, RowsUpdated = updated };
    }

    private async Task<MemoryDocIdReport> PlanAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var notes = new List<(string NoteId, string[] Paths, int UnmappedRows, Guid[] DocIds)>();
        await using (var cmd = new NpgsqlCommand(_notesSql, conn, tx))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                notes.Add((
                    reader.GetString(0),
                    reader.GetFieldValue<string[]>(1),
                    checked((int)reader.GetInt64(2)),
                    reader.GetFieldValue<Guid[]>(3)));
            }
        }

        var candidatesByPath = new Dictionary<string, List<(Guid DocId, string? FrontMatterId)>>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(_candidatesSql, conn, tx))
        {
            cmd.Parameters.Add(MemoryDocIdResolution.VaultIdParameter(vaultId));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (!candidatesByPath.TryGetValue(path, out var list))
                    candidatesByPath[path] = list = [];
                list.Add((reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var mappings = new List<MemoryDocIdMapping>();
        var unmapped = new List<MemoryDocIdIssue>();
        var conflicts = new List<MemoryDocIdIssue>();
        var alreadyMapped = new List<(string NoteId, Guid DocId)>();

        foreach (var (noteId, paths, unmappedRows, docIds) in notes)
        {
            var path = paths[0];
            if (paths.Length > 1)
            {
                conflicts.Add(new MemoryDocIdIssue(noteId, path, MemoryDocIdReasons.SeveralPaths));
                continue;
            }

            if (docIds.Length > 1)
            {
                conflicts.Add(new MemoryDocIdIssue(noteId, path, MemoryDocIdReasons.MappedToSeveralDocs));
                continue;
            }

            Guid? existing = docIds.Length == 1 ? docIds[0] : null;
            var (resolved, reason) = MemoryDocIdResolution.Decide(
                noteId,
                candidatesByPath.TryGetValue(path, out var found) ? found : []);

            if (existing is { } known)
            {
                // A doc id set once is kept. The index must agree, or the note is reported and left alone.
                if (resolved is { } now && now != known)
                {
                    conflicts.Add(new MemoryDocIdIssue(noteId, path, MemoryDocIdReasons.MappedToOtherDoc));
                }
                else if (unmappedRows == 0)
                {
                    alreadyMapped.Add((noteId, known));
                }
                else
                {
                    mappings.Add(new MemoryDocIdMapping(noteId, known, path, unmappedRows));
                }

                continue;
            }

            if (resolved is { } docId)
            {
                mappings.Add(new MemoryDocIdMapping(noteId, docId, path, unmappedRows));
            }
            else if (reason == MemoryDocIdReasons.NoVaultDoc)
            {
                unmapped.Add(new MemoryDocIdIssue(noteId, path, reason));
            }
            else
            {
                conflicts.Add(new MemoryDocIdIssue(noteId, path, reason!));
            }
        }

        // One doc node holds one note. A doc claimed by two notes is a conflict for every note that would newly map to it.
        var claims = mappings.Select(m => (m.NoteId, m.DocId))
            .Concat(alreadyMapped)
            .GroupBy(c => c.DocId)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(c => c.NoteId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var mapping in mappings.Where(m => claims.Contains(m.NoteId)).ToList())
        {
            mappings.Remove(mapping);
            conflicts.Add(new MemoryDocIdIssue(mapping.NoteId, mapping.VaultPath, MemoryDocIdReasons.DocClaimedTwice));
        }

        return new MemoryDocIdReport(
            DryRun: true,
            Mappings: mappings,
            Unmapped: unmapped,
            Conflicts: [.. conflicts.OrderBy(c => c.NoteId, StringComparer.Ordinal)],
            AlreadyMapped: alreadyMapped.Count,
            RowsUpdated: 0);
    }
}
