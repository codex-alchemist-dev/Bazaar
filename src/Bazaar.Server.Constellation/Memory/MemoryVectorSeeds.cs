using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Vault.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Why a retrieval ran on lexical seeds alone (docs/design/constellation-memory-graph.md, "Vector seeds", rule 7). The codes go into
/// the result and the audit entry; the query text never does.
/// </summary>
public static class MemoryLexicalOnlyReasons
{
    /// <summary><c>memory.embedding_profile</c> is <c>none</c>, no profile is active, or no vector seed source is wired.</summary>
    public const string ProfileNone = "profile_none";

    /// <summary>The embedder returned an empty vector, so it does not match the active profile.</summary>
    public const string ProfileMismatch = "profile_mismatch";

    /// <summary>The provider call failed.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>The provider call did not finish within the timeout.</summary>
    public const string ProviderTimeout = "provider_timeout";

    /// <summary>The gateway refused the call by policy or quota.</summary>
    public const string PolicyDenied = "policy_denied";

    /// <summary>The vector query against <c>vault_block_embedding</c> failed.</summary>
    public const string VectorQueryError = "vector_query_error";

    /// <summary>The query ran, but no block the caller may read has a vector under the active profile yet.</summary>
    public const string NoVectorsYet = "no_vectors_yet";
}

/// <summary>Vector seeds for one query: ranked doc ids (best block per doc), or none with the reason the call is lexical only.</summary>
/// <param name="Seeds">Doc ids ("D" format) ranked by their best block's cosine similarity, best first.</param>
/// <param name="LexicalOnlyReason">Null when the vector query ran and returned seeds; otherwise a <see cref="MemoryLexicalOnlyReasons"/> code.</param>
public sealed record MemoryVectorSeeds(IReadOnlyList<RankedHit> Seeds, string? LexicalOnlyReason)
{
    /// <summary>No seeds, for the given reason.</summary>
    public static MemoryVectorSeeds LexicalOnly(string reason) => new([], reason);
}

/// <summary>Produces vector seeds for a query (docs/design/constellation-memory-graph.md, "Vector seeds"). Never throws for an embedding or query failure.</summary>
public interface IMemoryVectorSeedSource
{
    /// <summary>
    /// Embeds <paramref name="queryText"/> once and returns the docs whose blocks are nearest under the active profile, restricted to
    /// what <paramref name="who"/> may read and to <paramref name="scopePrefixes"/> before ranking. A failure returns no seeds and a reason.
    /// </summary>
    Task<MemoryVectorSeeds> GetSeedsAsync(
        VaultPrincipal who,
        string queryText,
        IReadOnlyList<string>? scopePrefixes,
        int limit,
        CancellationToken ct);
}

/// <summary>One block-level vector candidate collapsed to its doc: the doc, its path and its best block's similarity.</summary>
public sealed record MemoryVectorCandidate(Guid DocId, string VaultPath, double Score);

/// <summary>The vector query over <c>vault_block_embedding</c>. Split out so the seed source can be tested without a database.</summary>
public interface IMemoryVectorQuery
{
    /// <summary>
    /// The docs nearest to <paramref name="vector"/> under <paramref name="profileId"/>, one row per doc (its best block), best first.
    /// The path filter is applied in SQL before the nearest-block limit, so excluded docs never take a place in the candidate pool.
    /// </summary>
    Task<IReadOnlyList<MemoryVectorCandidate>> NearestDocsAsync(
        Guid profileId,
        float[] vector,
        MemoryPathFilter filter,
        int limit,
        CancellationToken ct);
}

/// <summary>
/// The SQL-side path filter of a vector query, derived from the principal (docs/design/constellation-memory-graph.md, "Vector seeds",
/// rule 4). For an agent or cover, every <c>agents/</c> area except its own is excluded in the query, so another agent's private blocks
/// never enter the candidate set. A human member reads no agent area. The owner and maintenance are not restricted here. The full
/// <see cref="IVaultAccess"/> rule is applied again to each result.
/// </summary>
/// <param name="RestrictAgentAreas">True when <c>agents/</c> paths other than <paramref name="OwnAgentArea"/> are excluded.</param>
/// <param name="OwnAgentArea">The caller's own <c>agents/&lt;id&gt;/</c> prefix, or null.</param>
/// <param name="ScopePrefixes">Optional extra path prefixes; a doc must start with one of them.</param>
/// <param name="VaultId">The Vault whose docs are the graph (<c>Constellation:VaultId</c>), or null for any.</param>
public sealed record MemoryPathFilter(bool RestrictAgentAreas, string? OwnAgentArea, IReadOnlyList<string>? ScopePrefixes, Guid? VaultId)
{
    /// <summary>The filter for <paramref name="who"/>.</summary>
    public static MemoryPathFilter For(VaultPrincipal who, IReadOnlyList<string>? scopePrefixes, Guid? vaultId)
    {
        ArgumentNullException.ThrowIfNull(who);
        return who.Kind switch
        {
            VaultPrincipalKind.Owner or VaultPrincipalKind.Maintenance => new(false, null, scopePrefixes, vaultId),
            VaultPrincipalKind.Agent or VaultPrincipalKind.Cover => new(true, $"agents/{who.AgentId}/", scopePrefixes, vaultId),
            _ => new(true, null, scopePrefixes, vaultId),
        };
    }
}

/// <summary>Postgres <see cref="IMemoryVectorQuery"/>: cosine distance on <c>halfvec</c>, only rows whose block text is unchanged.</summary>
public sealed class PgMemoryVectorQuery(NpgsqlDataSource dataSource) : IMemoryVectorQuery
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    // A row is valid only while its block text is unchanged (text_hash), so stale vectors never seed. Profiles never mix.
    private const string _sql = """
        SELECT nearest.doc_id, nearest.path, max(nearest.score)::float8 AS score
        FROM (
            SELECT e.doc_id, d.path, 1 - (e.embedding <=> $1::text::halfvec) AS score
            FROM vault_block_embedding e
            JOIN vault_blocks b ON b.doc_id = e.doc_id AND b.ordinal = e.ordinal AND b.text_hash = e.text_hash
            JOIN vault_docs d ON d.doc_id = e.doc_id
            WHERE e.profile_id = $2
              AND ($3::uuid IS NULL OR d.vault_id = $3)
              AND (NOT $4::boolean OR NOT starts_with(d.path, 'agents/') OR ($5::text IS NOT NULL AND starts_with(d.path, $5::text)))
              AND ($6::text[] IS NULL OR EXISTS (SELECT 1 FROM unnest($6::text[]) AS p(prefix) WHERE starts_with(d.path, p.prefix)))
            ORDER BY e.embedding <=> $1::text::halfvec, e.doc_id, e.ordinal
            LIMIT $7
        ) AS nearest
        GROUP BY nearest.doc_id, nearest.path
        ORDER BY score DESC, nearest.doc_id
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryVectorCandidate>> NearestDocsAsync(
        Guid profileId,
        float[] vector,
        MemoryPathFilter filter,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var cmd = _dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ToVectorLiteral(vector) });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = profileId });
        AddFilterParameters(cmd, filter);
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

        var rows = new List<MemoryVectorCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new MemoryVectorCandidate(reader.GetGuid(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return rows;
    }

    /// <summary>Adds the four path filter parameters, in order: vault id, restrict-agent-areas flag, own agent area, scope prefixes.</summary>
    internal static void AddFilterParameters(NpgsqlCommand cmd, MemoryPathFilter filter)
    {
        cmd.Parameters.Add(MemoryDocIdResolution.VaultIdParameter(filter.VaultId));
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = filter.RestrictAgentAreas });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filter.OwnAgentArea ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = filter.ScopePrefixes is null ? DBNull.Value : filter.ScopePrefixes.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
    }

    /// <summary>A pgvector text literal, for example <c>[0.5,-1,2]</c>. Values must be finite.</summary>
    public static string ToVectorLiteral(float[] vector)
    {
        if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("A query vector must be non-empty and finite.", nameof(vector));
        }

        return "[" + string.Join(',', vector.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }
}

/// <summary>
/// Vector seeds through the Vault's query embedder contract (<see cref="IVaultQueryEmbedder"/>, the same one Vault search uses;
/// docs/design/constellation-memory-graph.md, "Vector seeds"). The query is embedded once under the embedder's profile, under a
/// timeout (default 5 s). With no embedder wired the source reports <see cref="MemoryLexicalOnlyReasons.ProfileNone"/>. Every
/// failure degrades to lexical only with a reason code and is logged without the query text; nothing is retried.
/// </summary>
public sealed partial class EmbeddingMemoryVectorSeedSource : IMemoryVectorSeedSource
{
    /// <summary>The default provider timeout for one query embedding.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IVaultQueryEmbedder? _embedder;
    private readonly IMemoryVectorQuery _query;
    private readonly IVaultAccess _access;
    private readonly Guid? _vaultId;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <param name="embedder">The query embedder, or null when no embedding profile is active.</param>
    /// <param name="query">The vector query over <c>vault_block_embedding</c>.</param>
    /// <param name="access">The Vault access layer, applied to every result.</param>
    /// <param name="vaultId">The Vault whose docs are the graph, or null for any.</param>
    /// <param name="timeout">The embedding timeout; defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public EmbeddingMemoryVectorSeedSource(
        IVaultQueryEmbedder? embedder,
        IMemoryVectorQuery query,
        IVaultAccess access,
        Guid? vaultId = null,
        TimeSpan? timeout = null,
        ILogger<EmbeddingMemoryVectorSeedSource>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(access);
        _embedder = embedder;
        _query = query;
        _access = access;
        _vaultId = vaultId;
        _timeout = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero, nameof(timeout));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<MemoryVectorSeeds> GetSeedsAsync(
        VaultPrincipal who,
        string queryText,
        IReadOnlyList<string>? scopePrefixes,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (_embedder is null)
        {
            return MemoryVectorSeeds.LexicalOnly(MemoryLexicalOnlyReasons.ProfileNone);
        }

        float[] vector;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_timeout);
            try
            {
                vector = await _embedder.EmbedAsync(queryText, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Degrade(MemoryLexicalOnlyReasons.ProviderTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Degrade(MemoryLexicalOnlyReasons.ProviderError);
            }
        }

        if (vector is null || vector.Length == 0)
        {
            return Degrade(MemoryLexicalOnlyReasons.ProfileMismatch);
        }

        if (vector.Any(value => !float.IsFinite(value)))
        {
            return Degrade(MemoryLexicalOnlyReasons.ProviderError);
        }

        IReadOnlyList<MemoryVectorCandidate> candidates;
        try
        {
            candidates = await _query.NearestDocsAsync(
                _embedder.ProfileId,
                vector,
                MemoryPathFilter.For(who, scopePrefixes, _vaultId),
                limit,
                ct).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            return Degrade(MemoryLexicalOnlyReasons.VectorQueryError);
        }

        // The access rule again, on every result, before anything is ranked: the SQL filter is a narrowing, not the authority.
        var seeds = new List<RankedHit>(candidates.Count);
        foreach (MemoryVectorCandidate candidate in candidates)
        {
            if (!VaultPath.TryParse(candidate.VaultPath, out VaultPath? path) || !_access.CanRead(who, path!))
            {
                continue;
            }

            if (scopePrefixes is not null && !scopePrefixes.Any(prefix => candidate.VaultPath.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            seeds.Add(new RankedHit(candidate.DocId.ToString("D"), candidate.Score));
        }

        return seeds.Count == 0
            ? MemoryVectorSeeds.LexicalOnly(MemoryLexicalOnlyReasons.NoVectorsYet)
            : new MemoryVectorSeeds(seeds, null);
    }

    private MemoryVectorSeeds Degrade(string reason)
    {
        LogLexicalOnly(_logger, reason);
        return MemoryVectorSeeds.LexicalOnly(reason);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Memory retrieval runs lexical only: {Reason}.")]
    private static partial void LogLexicalOnly(ILogger logger, string reason);
}
