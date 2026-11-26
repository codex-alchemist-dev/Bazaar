using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// The one rule that maps a memory note to its Vault doc node (docs/design/constellation-memory-graph.md, "Convergence design",
/// item 1). The note's vault path must match exactly one <c>vault_docs</c> row (in the configured Vault when one is given), and
/// that doc's front matter <c>id</c>, when present, must equal the note id. Anything else maps to nothing.
/// </summary>
public static class MemoryDocIdResolution
{
    /// <summary>Candidate docs for one path. Two rows are enough to tell that the path is ambiguous.</summary>
    internal const string CandidatesSql = """
        SELECT doc_id, frontmatter->>'id'
        FROM vault_docs
        WHERE path = $1 AND ($2::uuid IS NULL OR vault_id = $2)
        ORDER BY doc_id
        LIMIT 2
        """;

    /// <summary>
    /// Applies the rule to the candidate rows of one path. Returns the doc id, or null with the reason code of
    /// <see cref="MemoryDocIdReasons"/>.
    /// </summary>
    public static (Guid? DocId, string? Reason) Decide(string noteId, IReadOnlyList<(Guid DocId, string? FrontMatterId)> candidates)
    {
        ArgumentNullException.ThrowIfNull(noteId);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return (null, MemoryDocIdReasons.NoVaultDoc);
        if (candidates.Count > 1)
            return (null, MemoryDocIdReasons.AmbiguousPath);

        var (docId, frontMatterId) = candidates[0];
        if (!string.IsNullOrEmpty(frontMatterId) && !string.Equals(frontMatterId, noteId, StringComparison.OrdinalIgnoreCase))
            return (null, MemoryDocIdReasons.FrontMatterIdMismatch);
        return (docId, null);
    }

    /// <summary>Resolves the doc id of one note inside the caller's transaction. Null when the rule maps nothing.</summary>
    internal static async Task<Guid?> ResolveAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string noteId,
        string vaultPath,
        Guid? vaultId,
        CancellationToken ct)
    {
        var candidates = new List<(Guid, string?)>(2);
        await using (var cmd = new NpgsqlCommand(CandidatesSql, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = vaultPath });
            cmd.Parameters.Add(VaultIdParameter(vaultId));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                candidates.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return Decide(noteId, candidates).DocId;
    }

    /// <summary>A nullable uuid parameter for the optional Vault filter.</summary>
    internal static NpgsqlParameter VaultIdParameter(Guid? vaultId) =>
        new() { Value = (object?)vaultId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid };
}

/// <summary>Reason codes for a note that maps to no doc, used by the store and reported by <see cref="MemoryGraphDocIdMigrator"/>.</summary>
public static class MemoryDocIdReasons
{
    /// <summary>No Vault doc has the note's path.</summary>
    public const string NoVaultDoc = "no_vault_doc";

    /// <summary>More than one Vault doc has the note's path (several Vaults and no Vault configured).</summary>
    public const string AmbiguousPath = "ambiguous_path";

    /// <summary>The doc at the path names a different id in its front matter.</summary>
    public const string FrontMatterIdMismatch = "front_matter_id_mismatch";

    /// <summary>The note's revisions are already mapped to a different doc than the index now gives.</summary>
    public const string MappedToOtherDoc = "mapped_to_other_doc";

    /// <summary>The note's revisions are mapped to more than one doc.</summary>
    public const string MappedToSeveralDocs = "mapped_to_several_docs";

    /// <summary>The note's revisions name more than one vault path.</summary>
    public const string SeveralPaths = "several_paths";

    /// <summary>Two notes would map to the same doc.</summary>
    public const string DocClaimedTwice = "doc_claimed_twice";
}
