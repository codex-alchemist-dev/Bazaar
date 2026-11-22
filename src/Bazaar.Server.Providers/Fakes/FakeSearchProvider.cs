namespace Bazaar.Server.Providers.Fakes;

/// <summary>In-memory search provider for tests. Matches by case-insensitive substring within one scope.</summary>
public sealed class FakeSearchProvider : ISearchProvider
{
    private readonly bool _usable;
    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates a fake search provider with the given kind and usability.</summary>
    public FakeSearchProvider(string kind = "fake_search", bool usable = true)
    {
        Kind = kind;
        _usable = usable;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public ProviderStatus Probe() =>
        new(Kind, _usable, Array.Empty<string>(), _usable ? null : "fake provider unavailable");

    /// <inheritdoc />
    public Task IndexAsync(SearchDocument doc, CancellationToken ct)
    {
        lock (_gate)
        {
            _documents[doc.Id] = doc;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.Limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>());
        }

        List<SearchHit> hits;
        lock (_gate)
        {
            hits = _documents.Values
                .Where(d => d.Scope == query.Scope
                    && d.Text.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Take(query.Limit)
                .Select(d => new SearchHit(d.Id, 1.0))
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
    }
}
