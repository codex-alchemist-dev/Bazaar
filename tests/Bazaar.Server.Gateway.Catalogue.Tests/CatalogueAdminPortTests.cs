using System.Text.Json;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Gateway.Catalogue.Tests;

public sealed class CatalogueAdminPortTests
{
    private sealed class FakeRefresher(IReadOnlyList<CatalogueRefreshResult> results) : ICatalogueRefresher
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CatalogueRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(results);
        }

        public Task<CatalogueRefreshResult> RefreshProviderAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(results[0]);
    }

    private static CatalogueRefreshResult Result(string id, CatalogueRefreshOutcome outcome) =>
        new(id, outcome, new CatalogueDiff(["n1", "n2"], ["m1"], [], ["u1"]), ["n2"]);

    [Fact]
    public async Task Refresh_maps_outcomes_counts_and_unclassified_ids()
    {
        var last = new CatalogueLastRun();
        var results = new[] { Result("a", CatalogueRefreshOutcome.Refreshed), Result("b", CatalogueRefreshOutcome.ListFailed), Result("c", CatalogueRefreshOutcome.Skipped) };
        var port = new CatalogueAdminPort(new RecordingCatalogueRefresher(new FakeRefresher(results), last), last);

        var report = await port.RefreshAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(["refreshed", "listFailed", "skipped"], report.Providers.Select(p => p.Outcome));
        var a = report.Providers[0];
        Assert.Equal((2, 1, 0), (a.Added, a.Missing, a.Returned));
        Assert.Equal(["n2"], a.UnclassifiedIds);
        Assert.NotNull(report.RanAt);
    }

    [Fact]
    public async Task Last_is_empty_until_a_refresh_runs_then_returns_it_including_scheduled_runs()
    {
        var last = new CatalogueLastRun();
        var recording = new RecordingCatalogueRefresher(new FakeRefresher([Result("a", CatalogueRefreshOutcome.Refreshed)]), last);
        var port = new CatalogueAdminPort(recording, last);

        var before = await port.GetLastAsync(TestContext.Current.CancellationToken);
        Assert.Null(before.RanAt);
        Assert.Empty(before.Providers);

        await recording.RefreshAllAsync(TestContext.Current.CancellationToken);
        var after = await port.GetLastAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(after.RanAt);
        Assert.Equal("a", Assert.Single(after.Providers).ProviderId);
    }

    [Fact]
    public async Task Report_carries_only_ids_and_counts_never_secrets()
    {
        var last = new CatalogueLastRun();
        var port = new CatalogueAdminPort(new RecordingCatalogueRefresher(new FakeRefresher([Result("a", CatalogueRefreshOutcome.Refreshed)]), last), last);

        var report = await port.RefreshAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        var names = typeof(AdminCatalogueProviderResult).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(["ProviderId", "Outcome", "Added", "Missing", "Returned", "UnclassifiedIds"], names);
        Assert.DoesNotContain("http", JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);
    }
}
