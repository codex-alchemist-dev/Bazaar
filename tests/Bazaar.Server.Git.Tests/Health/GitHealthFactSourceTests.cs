using Bazaar.Server.Git.Forge;
using Bazaar.Server.Git.Health;
using Bazaar.Server.Health;

namespace Bazaar.Server.Git.Tests.Health;

public class GitHealthFactSourceTests
{
    private static readonly DateOnly _day = new(2026, 11, 22);
    private static readonly DateTimeOffset _noon = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Directory(params GitRepositoryInfo[] repos) : IGitRepositoryDirectory
    {
        public Task<GitRepositoryInfo?> FindAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult(repos.FirstOrDefault(r => r.RepositoryId == repositoryId));

        public Task<IReadOnlyList<GitRepositoryInfo>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitRepositoryInfo>>(repos);
    }

    /// <summary>A forge whose checks depend on the PR head sha.</summary>
    private sealed class Forge(List<PrSummary> prs, Func<string, IReadOnlyList<CheckRun>> checks) : FakeForgeBase
    {
        public int CheckCalls { get; private set; }

        public override Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<PrSummary>([.. prs.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)], query.Page * query.PageSize < prs.Count));

        public override Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
        {
            CheckCalls++;
            return Task.FromResult(checks(headSha));
        }
    }

    private static PrSummary Pr(long n, string sha, DateTimeOffset at, string title = "feat: thing", PrState state = PrState.Open) =>
        new(new PrRef("fake", "acme/widgets", n), title, "bob", state, false, sha, new BranchName("feat"), new BranchName("main"), at);

    private static CheckRun Check(CheckState s, string sha = "x") => new("ci", s, sha, null);

    private static GitHealthFactSource Source(Forge forge, GitHealthOptions? options = null) =>
        new(new ForgeRegistry([forge], []), new Directory(new GitRepositoryInfo("repo-1", new RepoRef("fake", "acme/widgets"), "/tmp/x", null, null)), options);

    [Fact]
    public async Task A_finished_pr_is_a_ci_run_passed_when_no_check_failed()
    {
        var forge = new Forge(
            [Pr(1, "aaaaaaaa1", _noon), Pr(2, "bbbbbbbb2", _noon.AddHours(1)), Pr(3, "cccccccc3", _noon)],
            sha => sha switch
            {
                "aaaaaaaa1" => [Check(CheckState.Success), Check(CheckState.Skipped)],
                "bbbbbbbb2" => [Check(CheckState.Success), Check(CheckState.Failure)],
                _ => [Check(CheckState.Success), Check(CheckState.Pending)],
            });
        var facts = await Source(forge).ReadAsync("repo-1", _day, Ct);

        Assert.Equal(2, facts.CiRuns.Count);
        Assert.True(facts.CiRuns.Single(r => r.RunId.Contains("#1@")).Passed);
        Assert.False(facts.CiRuns.Single(r => r.RunId.Contains("#2@")).Passed);
        Assert.DoesNotContain(facts.CiRuns, r => r.RunId.Contains("#3@"));
    }

    [Fact]
    public async Task Prs_updated_on_other_days_are_ignored_and_not_checked()
    {
        var forge = new Forge([Pr(1, "a", _noon.AddDays(-1)), Pr(2, "b", _noon.AddDays(1))], _ => [Check(CheckState.Success)]);
        var facts = await Source(forge).ReadAsync("repo-1", _day, Ct);

        Assert.Empty(facts.CiRuns);
        Assert.Equal(0, forge.CheckCalls);
    }

    [Fact]
    public async Task A_pr_with_no_checks_is_not_a_run()
    {
        var forge = new Forge([Pr(1, "a", _noon)], _ => []);

        Assert.Empty((await Source(forge).ReadAsync("repo-1", _day, Ct)).CiRuns);
    }

    [Fact]
    public async Task Merged_prs_titled_revert_are_reverts()
    {
        var forge = new Forge(
            [Pr(1, "a", _noon, "Revert \"feat: x\"", PrState.Merged), Pr(2, "b", _noon, "revert: y", PrState.Merged), Pr(3, "c", _noon, "Revert z", PrState.Open), Pr(4, "d", _noon, "feat", PrState.Merged)],
            _ => []);
        var facts = await Source(forge).ReadAsync("repo-1", _day, Ct);

        Assert.Equal(["a", "b"], facts.Reverts.Select(r => r.Sha).Order());
    }

    [Fact]
    public async Task Unknown_repository_or_provider_is_a_gap()
    {
        var forge = new Forge([Pr(1, "a", _noon)], _ => [Check(CheckState.Success)]);
        var other = new GitHealthFactSource(new ForgeRegistry([forge], []), new Directory(new GitRepositoryInfo("r", new RepoRef("gitlab", "g/p"), "/tmp", null, null)));

        Assert.Same(HealthFacts.Empty, await Source(forge).ReadAsync("nope", _day, Ct));
        Assert.Same(HealthFacts.Empty, await other.ReadAsync("r", _day, Ct));
    }

    [Fact]
    public async Task Page_and_check_limits_bound_the_forge_calls()
    {
        var prs = Enumerable.Range(1, 10).Select(n => Pr(n, "s" + n, _noon.AddMinutes(n))).ToList();
        var forge = new Forge(prs, _ => [Check(CheckState.Success)]);
        var facts = await Source(forge, new GitHealthOptions { MaxPages = 1, PageSize = 4, MaxChecksPerDay = 3 }).ReadAsync("repo-1", _day, Ct);

        Assert.Equal(3, forge.CheckCalls);
        Assert.Equal(3, facts.CiRuns.Count);
        Assert.Equal(["acme/widgets#1@s1", "acme/widgets#2@s2", "acme/widgets#3@s3"], facts.CiRuns.Select(r => r.RunId));
    }

    [Fact]
    public async Task A_forge_failure_propagates_so_the_job_retries()
    {
        var forge = new Forge([Pr(1, "a", _noon)], _ => throw new ForgeRateLimitedException(null));

        await Assert.ThrowsAsync<ForgeRateLimitedException>(() => Source(forge).ReadAsync("repo-1", _day, Ct));
    }

    [Fact]
    public async Task Every_registered_repository_is_a_health_project()
    {
        var dir = new Directory(new GitRepositoryInfo("r1", new RepoRef("fake", "a/b"), "/x", null, null), new GitRepositoryInfo("r2", new RepoRef("fake", "a/c"), "/y", null, null));

        Assert.Equal(["r1", "r2"], await new GitHealthProjectSource(dir).ListProjectsAsync(Ct));
    }
}

/// <summary>An <see cref="IForgeProvider"/> whose members all throw, for a test to override the ones it needs.</summary>
internal abstract class FakeForgeBase : IForgeProvider
{
    public string ProviderId => "fake";
    public Task<PrSummary> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PrDetail> GetPrAsync(PrRef pr, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
