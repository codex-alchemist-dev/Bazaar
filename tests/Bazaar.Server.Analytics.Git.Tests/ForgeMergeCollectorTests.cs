using Bazaar.Server.Analytics.Postgres;
using Bazaar.Server.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Analytics.Git.Tests;

public class ForgeMergeCollectorTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly AnalyticsWindow _window = new(_now.AddDays(-7), _now);
    private static readonly RepoRef _repo = new("github", "o/r");

    private static PrSummary Pr(int n, string login, PrState state, int daysAgo) =>
        new(new PrRef("github", "o/r", n), "t", login, state, false, "sha", new BranchName("h"), new BranchName("main"), _now.AddDays(-daysAgo));

    private static ForgeMergeCollector Collector(FakeForge forge, params (string Login, string Agent)[] map) => new(
        new FakeRepos(), new FakeRegistry(forge), new ConfiguredForgeAuthorResolver(map.ToDictionary(m => m.Login, m => m.Agent)));

    [Fact]
    public async Task Counts_merged_prs_in_the_window_for_mapped_authors_only()
    {
        var forge = new FakeForge([[Pr(1, "Amy-Bot", PrState.Merged, 1), Pr(2, "amy-bot", PrState.Merged, 30), Pr(3, "stranger", PrState.Merged, 1), Pr(4, "amy-bot", PrState.Closed, 1)]]);

        var events = await Collector(forge, ("amy-bot", "amy")).CollectAsync(_window, TestContext.Current.CancellationToken);

        var e = Assert.Single(events);
        Assert.Equal(new AgentWorkEvent("amy", AgentWorkEventKind.PrMerged, _now.AddDays(-1)), e);
    }

    [Fact]
    public async Task Reads_further_pages_while_the_forge_has_more_and_stops_at_the_cap()
    {
        var pages = Enumerable.Range(1, 8).Select(i => new[] { Pr(i, "amy-bot", PrState.Merged, 1) }).ToArray();
        var forge = new FakeForge(pages);

        var events = await Collector(forge, ("amy-bot", "amy")).CollectAsync(_window, TestContext.Current.CancellationToken);

        Assert.Equal(5, events.Count);
        Assert.Equal(5, forge.PagesRead);
    }

    [Fact]
    public void Merge_source_setting_picks_exactly_one_counter()
    {
        static IConfiguration Cfg(string? source) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Bazaar:Analytics:MergeSource"] = source, ["Bazaar:Analytics:ForgeAuthors:amy-bot"] = "amy" }).Build();
        static int Collectors(string? source)
        {
            var services = new ServiceCollection();
            services.AddSingleton(NpgsqlDataSource.Create("Host=localhost"));
            new AnalyticsPostgresComponent().AddServices(services, Cfg(source));
            new AnalyticsGitComponent().AddServices(services, Cfg(source));
            return services.Count(d => d.ServiceType == typeof(IWorkEventCollector));
        }

        Assert.Equal(2, Collectors(null));      // Kanban + revert
        Assert.Equal(3, Collectors("forge"));   // Kanban (no merges) + revert + forge
    }

    private sealed class FakeForge(PrSummary[][] pages) : IForgeProvider
    {
        public int PagesRead { get; private set; }

        public string ProviderId => "github";

        public Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken ct)
        {
            PagesRead++;
            return Task.FromResult(new PagedResult<PrSummary>(pages[query.Page - 1], query.Page < pages.Length));
        }

        public Task<PrSummary> OpenDraftPrAsync(OpenPrRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<PrDetail> GetPrAsync(PrRef pr, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken ct) => throw new NotSupportedException();

        public Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRegistry(IForgeProvider forge) : IForgeRegistry
    {
        public IReadOnlyCollection<string> ProviderIds => ["github"];

        public IForgeProvider Resolve(string providerId) => forge;

        public IForgeMergeExecutor ResolveMergeExecutor(string providerId) => throw new NotSupportedException();
    }

    private sealed class FakeRepos : IGitRepositoryDirectory
    {
        public Task<GitRepositoryInfo?> FindAsync(string repositoryId, CancellationToken ct) => Task.FromResult<GitRepositoryInfo?>(null);

        public Task<IReadOnlyList<GitRepositoryInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitRepositoryInfo>>([new GitRepositoryInfo("r1", _repo, "/tmp/r", null, null)]);
    }
}
