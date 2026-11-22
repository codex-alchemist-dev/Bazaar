namespace Bazaar.Server.Git.Tests.Engine;

public class RepoLocksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_acquires_on_one_key_serialise()
    {
        var locks = new RepoLocks(TimeSpan.FromSeconds(10));
        var first = await locks.AcquireAsync("repo-a", Ct);

        var second = locks.AcquireAsync("repo-a", Ct);
        await Task.Delay(100, Ct);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var lease = await second;
    }

    [Fact]
    public async Task Different_keys_run_in_parallel()
    {
        var locks = new RepoLocks(TimeSpan.FromSeconds(10));
        using var a = await locks.AcquireAsync("repo-a", Ct);
        using var b = await locks.AcquireAsync("repo-b", Ct);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Timeout_throws_and_the_holder_keeps_the_lock()
    {
        var locks = new RepoLocks(TimeSpan.FromMilliseconds(100));
        using var held = await locks.AcquireAsync("repo-a", Ct);

        await Assert.ThrowsAsync<GitLockTimeoutException>(() => locks.AcquireAsync("repo-a", Ct));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_leak_the_lock()
    {
        var locks = new RepoLocks(TimeSpan.FromSeconds(10));
        var held = await locks.AcquireAsync("repo-a", Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = locks.AcquireAsync("repo-a", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        held.Dispose();
        using var again = await locks.AcquireAsync("repo-a", Ct);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Already_cancelled_token_takes_nothing()
    {
        var locks = new RepoLocks(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireAsync("repo-a", cts.Token));

        using var lease = await locks.AcquireAsync("repo-a", Ct);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task Double_dispose_releases_once()
    {
        var locks = new RepoLocks(TimeSpan.FromMilliseconds(100));
        var first = await locks.AcquireAsync("repo-a", Ct);
        first.Dispose();
        first.Dispose();

        using var second = await locks.AcquireAsync("repo-a", Ct);
        await Assert.ThrowsAsync<GitLockTimeoutException>(() => locks.AcquireAsync("repo-a", Ct));
    }

    [Fact]
    public async Task Merge_lane_is_independent_of_the_repo_lock_and_single_per_repo()
    {
        var locks = new RepoLocks(TimeSpan.FromMilliseconds(100));
        using var repoLock = await locks.AcquireAsync("repo-a", Ct);
        using var lane = await locks.AcquireMergeLaneAsync("repo-a", Ct);
        await Assert.ThrowsAsync<GitLockTimeoutException>(() => locks.AcquireMergeLaneAsync("repo-a", Ct));

        using var otherLane = await locks.AcquireMergeLaneAsync("repo-b", Ct);
        Assert.NotNull(otherLane);
    }

    [Fact]
    public async Task Provider_calls_on_one_repo_wait_for_the_lock_and_time_out()
    {
        using var fixture = new EngineFixture(TimeSpan.FromMilliseconds(150));
        using var held = await fixture.Locks.AcquireAsync(RepoLocks.KeyForPath(fixture.CloneRoot), Ct);

        await Assert.ThrowsAsync<GitLockTimeoutException>(() => fixture.Provider.FetchAsync(EngineFixture.Repo, Ct));
        await Assert.ThrowsAsync<GitLockTimeoutException>(
            () => fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/x"), Ct));
    }

    [Fact]
    public async Task Provider_call_on_another_repo_is_not_blocked()
    {
        using var fixture = new EngineFixture(TimeSpan.FromMilliseconds(150));
        // Hold a lock for some other repo path; a call on the fixture's repo still goes through.
        using var held = await fixture.Locks.AcquireAsync(RepoLocks.KeyForPath(Path.Combine(fixture.Root, "other")), Ct);

        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/parallel"), Ct);
        Assert.True(Directory.Exists(handle.Path));
    }

    [Fact]
    public async Task Provider_cancellation_while_waiting_releases_the_lock()
    {
        using var fixture = new EngineFixture();
        var key = RepoLocks.KeyForPath(fixture.CloneRoot);
        var held = await fixture.Locks.AcquireAsync(key, Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = fixture.Provider.FetchAsync(EngineFixture.Repo, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        held.Dispose();
        await fixture.Provider.FetchAsync(EngineFixture.Repo, Ct);
    }
}
