namespace Bazaar.Server.Providers;

/// <summary>Search over indexed documents. Postgres FTS is always usable, so every install has a search provider.</summary>
public interface ISearchProvider : IProvider
{
    Task IndexAsync(SearchDocument doc, CancellationToken ct);

    Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery query, CancellationToken ct);
}

/// <summary>A document to index. <paramref name="Scope"/> keeps one owner's documents apart from another's.</summary>
public sealed record SearchDocument(string Id, string Scope, string Text);

/// <summary>A text query inside one scope, returning at most <paramref name="Limit"/> hits.</summary>
public sealed record SearchQuery(string Scope, string Text, int Limit);

/// <summary>One match. Higher <paramref name="Score"/> ranks first.</summary>
public sealed record SearchHit(string Id, double Score);
