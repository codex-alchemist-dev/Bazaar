using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Search.PgFts;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Providers.Search.PgFts;

/// <summary>Contract tests for the Postgres full-text provider, on a real server. Each test uses its own scope.</summary>
public sealed class PgFtsSearchProviderTests(SearchDb db) : IClassFixture<SearchDb>
{
    private PgFtsSearchProvider Provider => new(db.DataSource);

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewScope() => $"scope-{Guid.NewGuid():N}";

    private static async Task<IReadOnlyList<string>> IdsAsync(PgFtsSearchProvider provider, string scope, string text, int limit = 10) =>
        (await provider.QueryAsync(new SearchQuery(scope, text, limit), Ct)).Select(h => h.Id).ToList();

    [Fact]
    public void Probe_reports_the_provider_as_usable()
    {
        // Probe does not use the database, so it runs on Windows without one.
        var status = new PgFtsSearchProvider(null!).Probe();

        Assert.Equal("postgres_fts", status.Kind);
        Assert.True(status.Usable);
    }

    [Fact]
    public async Task An_indexed_document_is_found_by_a_word_in_its_body()
    {
        SkipWithoutDatabase();
        var scope = NewScope();
        await Provider.IndexAsync(new SearchDocument("d1", scope, "the quarterly budget review"), Ct);

        Assert.Equal(["d1"], await IdsAsync(Provider, scope, "budget"));
    }

    [Fact]
    public async Task Scopes_do_not_see_each_others_documents()
    {
        SkipWithoutDatabase();
        var mine = NewScope();
        var theirs = NewScope();
        await Provider.IndexAsync(new SearchDocument("d1", mine, "shared phrase here"), Ct);

        Assert.Empty(await IdsAsync(Provider, theirs, "shared"));
        Assert.Equal(["d1"], await IdsAsync(Provider, mine, "shared"));
    }

    [Fact]
    public async Task Indexing_the_same_id_replaces_the_text()
    {
        SkipWithoutDatabase();
        var scope = NewScope();
        await Provider.IndexAsync(new SearchDocument("d1", scope, "old wording"), Ct);
        await Provider.IndexAsync(new SearchDocument("d1", scope, "new wording"), Ct);

        Assert.Empty(await IdsAsync(Provider, scope, "old"));
        Assert.Equal(["d1"], await IdsAsync(Provider, scope, "new"));
    }

    [Fact]
    public async Task Hits_are_limited_and_ordered_by_score_descending()
    {
        SkipWithoutDatabase();
        var scope = NewScope();
        await Provider.IndexAsync(new SearchDocument("once", scope, "alpha beta"), Ct);
        await Provider.IndexAsync(new SearchDocument("thrice", scope, "alpha alpha alpha beta"), Ct);
        await Provider.IndexAsync(new SearchDocument("other", scope, "gamma delta"), Ct);

        var hits = await Provider.QueryAsync(new SearchQuery(scope, "alpha", 1), Ct);
        Assert.Single(hits);

        var all = await Provider.QueryAsync(new SearchQuery(scope, "alpha", 10), Ct);
        Assert.Equal(2, all.Count);
        Assert.True(all[0].Score >= all[1].Score);
    }

    [Fact]
    public async Task Websearch_negation_excludes_documents_with_the_word()
    {
        SkipWithoutDatabase();
        var scope = NewScope();
        await Provider.IndexAsync(new SearchDocument("with-beta", scope, "alpha beta"), Ct);
        await Provider.IndexAsync(new SearchDocument("with-gamma", scope, "alpha gamma"), Ct);

        Assert.Equal(["with-gamma"], await IdsAsync(Provider, scope, "alpha -beta"));
    }

    [Fact]
    public async Task No_match_returns_nothing()
    {
        SkipWithoutDatabase();
        var scope = NewScope();
        await Provider.IndexAsync(new SearchDocument("d1", scope, "something else"), Ct);

        Assert.Empty(await IdsAsync(Provider, scope, "absent"));
    }

    [Fact]
    public async Task A_blank_query_or_zero_limit_returns_nothing()
    {
        SkipWithoutDatabase();
        var scope = NewScope();
        await Provider.IndexAsync(new SearchDocument("d1", scope, "words"), Ct);

        Assert.Empty(await IdsAsync(Provider, scope, "   "));
        Assert.Empty(await IdsAsync(Provider, scope, "words", limit: 0));
    }

    [Fact]
    public async Task Empty_document_ids_and_scopes_are_rejected()
    {
        SkipWithoutDatabase();
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.IndexAsync(new SearchDocument("", NewScope(), "x"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.IndexAsync(new SearchDocument("d1", "", "x"), Ct));
    }
}
