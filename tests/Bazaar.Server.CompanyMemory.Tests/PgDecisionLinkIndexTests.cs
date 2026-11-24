using Bazaar.Server.Knowledge.CompanyMemory;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.CompanyMemory.Tests;

/// <summary>The Postgres decision link index against a real PostgreSQL, matching the in-memory one.</summary>
public sealed class PgDecisionLinkIndexTests(CompanyMemoryDb db) : IClassFixture<CompanyMemoryDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DecisionLink _ab = new("decisions/0002-b.md", "decisions/0001-a.md", "supersedes", "accepted", false, false);
    private static readonly DecisionLink _bc = new("decisions/0003-c.md", "decisions/0002-b.md", "supersedes", null, true, false);

    private async Task<PgDecisionLinkIndex> FreshAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await db.ClearAsync();
        return new PgDecisionLinkIndex(db.DataSource);
    }

    [Fact]
    public async Task Edges_round_trip_and_are_found_from_either_end()
    {
        var index = await FreshAsync();
        await index.ReplaceAllAsync([_ab, _bc], Ct);

        var touching = await index.LinksTouchingAsync("decisions/0002-b.md", Ct);

        Assert.Equal([_ab, _bc], touching);
        Assert.Single(await index.LinksTouchingAsync("decisions/0001-a.md", Ct));
        Assert.Empty(await index.LinksTouchingAsync("decisions/0009-x.md", Ct));
    }

    [Fact]
    public async Task Replacing_drops_old_edges_and_an_empty_list_clears()
    {
        var index = await FreshAsync();
        await index.ReplaceAllAsync([_ab, _bc], Ct);
        await index.ReplaceAllAsync([_ab], Ct);

        Assert.Empty(await index.LinksTouchingAsync("decisions/0003-c.md", Ct));

        await index.ReplaceAllAsync([], Ct);

        Assert.Empty(await index.LinksTouchingAsync("decisions/0001-a.md", Ct));
    }

    [Fact]
    public async Task Recording_a_decision_refreshes_the_index()
    {
        var index = await FreshAsync();
        var memory = new CompanyMemoryService(new InMemoryCompanyDecisionStore(), index);

        await memory.RecordDecisionAsync(new(Guid.NewGuid(), "a", "A", "Body.", "agent-ana"), Ct);
        await memory.RecordDecisionAsync(new(Guid.NewGuid(), "b", "B", "Body.", "agent-ana", ["0001"]), Ct);

        var links = await index.LinksTouchingAsync("decisions/0001-a.md", Ct);
        Assert.Equal("decisions/0002-b.md", links.Single().FromDoc);
    }
}
