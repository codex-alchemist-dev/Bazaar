using Npgsql;

namespace Bazaar.Server.Providers.Search.PgFts;

/// <summary>
/// Postgres full-text search (doc 03 WP10). OS-neutral, so it is the always-available search provider. Documents are
/// indexed with the <c>simple</c> configuration (no stemming), which keeps names and identifiers exact.
/// </summary>
public sealed class PgFtsSearchProvider(NpgsqlDataSource dataSource) : ISearchProvider
{
    private const int _maxIdLength = 200;

    private const string _indexSql = """
        INSERT INTO search_document (scope, doc_id, body, updated_at)
        VALUES (@scope, @doc_id, @body, now())
        ON CONFLICT (scope, doc_id) DO UPDATE SET body = EXCLUDED.body, updated_at = now()
        """;

    private const string _querySql = """
        WITH q AS (SELECT websearch_to_tsquery('simple', @query) AS query)
        SELECT d.doc_id, ts_rank(d.tsv, q.query) AS score
        FROM search_document AS d, q
        WHERE d.scope = @scope AND d.tsv @@ q.query
        ORDER BY score DESC, d.doc_id
        LIMIT @limit
        """;

    /// <inheritdoc />
    public string Kind => "postgres_fts";

    /// <inheritdoc />
    public ProviderStatus Probe() => new(Kind, true, ["tsvector", "ts_rank"], null);

    /// <inheritdoc />
    public async Task IndexAsync(SearchDocument doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Id.Length == 0 || doc.Id.Length > _maxIdLength)
            throw new ArgumentException($"Document id must be 1 to {_maxIdLength} characters.", nameof(doc));
        if (doc.Scope.Length == 0 || doc.Scope.Length > _maxIdLength)
            throw new ArgumentException($"Scope must be 1 to {_maxIdLength} characters.", nameof(doc));

        await using var cmd = dataSource.CreateCommand(_indexSql);
        cmd.Parameters.AddWithValue("scope", doc.Scope);
        cmd.Parameters.AddWithValue("doc_id", doc.Id);
        cmd.Parameters.AddWithValue("body", doc.Text);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit <= 0 || string.IsNullOrWhiteSpace(query.Text))
            return [];

        await using var cmd = dataSource.CreateCommand(_querySql);
        cmd.Parameters.AddWithValue("query", query.Text);
        cmd.Parameters.AddWithValue("scope", query.Scope);
        cmd.Parameters.AddWithValue("limit", query.Limit);
        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(new SearchHit(reader.GetString(0), reader.GetFloat(1)));
        }

        return hits;
    }
}
