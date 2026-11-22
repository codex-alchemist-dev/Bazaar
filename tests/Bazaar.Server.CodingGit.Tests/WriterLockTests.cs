using Bazaar.Server.CodingGit.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingGit.Tests;

public sealed class WriterLockTests
{
    [Fact]
    public async Task Second_writer_on_one_worktree_fails_fast_with_a_typed_error()
    {
        using var fx = new CodingGitFixture();
        await using var first = await fx.OpenAsync();

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeBusy, ex.Error);
    }

    [Fact]
    public async Task Same_branch_for_another_agent_is_refused_while_held()
    {
        using var fx = new CodingGitFixture();
        await using var first = await fx.OpenAsync();

        var other = fx.Service.CreateWorktreeAsync(CodingGitFixture.Request(agent: "ivy"));
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => other);
        Assert.Equal(CodingGitError.WorktreeBusy, ex.Error);
    }

    [Fact]
    public async Task Disposing_the_worktree_frees_it_for_the_next_writer_and_kills_the_old_handle()
    {
        using var fx = new CodingGitFixture();
        var first = await fx.OpenAsync();
        await first.DisposeAsync();
        await first.DisposeAsync(); // idempotent

        await using var second = await fx.OpenAsync();
        Assert.True(second.IsActive);

        fx.Engine.StageChange(second.Path, "a.txt", "+a");
        var ex = await Assert.ThrowsAsync<CodingGitException>(() =>
            fx.Service.CommitAsync(first, new CommitInput("x", null, CodingGitFixture.Model)));
        Assert.Equal(CodingGitError.LeaseLost, ex.Error);
        Assert.Empty(fx.Engine.Commits);
    }

    [Fact]
    public async Task Concurrent_operation_on_one_worktree_fails_fast()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();
        fx.Engine.StageChange(wt.Path, "a.txt", "+a");

        var hold = new TaskCompletionSource();
        fx.Engine.Gate = hold.Task;
        var running = fx.Service.CommitAsync(wt, new CommitInput("one", null, CodingGitFixture.Model));

        var ex = await Assert.ThrowsAsync<CodingGitException>(() =>
            fx.Service.DiffAgainstBaseAsync(wt));
        Assert.Equal(CodingGitError.WorktreeBusy, ex.Error);

        hold.SetResult();
        await running;
    }

    [Fact]
    public async Task Lock_is_released_after_the_engine_throws()
    {
        using var fx = new CodingGitFixture();
        fx.Engine.FailNextWith = new InvalidOperationException("disk full");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.OpenAsync());

        await using var wt = await fx.OpenAsync();
        Assert.True(wt.IsActive);
    }

    [Fact]
    public async Task Lock_is_released_after_cancellation_before_and_during_the_engine_call()
    {
        using var fx = new CodingGitFixture();

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fx.Service.CreateWorktreeAsync(CodingGitFixture.Request(), already.Token));

        using var during = new CancellationTokenSource();
        fx.Engine.Gate = new TaskCompletionSource().Task; // never completes
        var pending = fx.Service.CreateWorktreeAsync(CodingGitFixture.Request(), during.Token);
        await during.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        fx.Engine.Gate = null;
        await using var wt = await fx.OpenAsync();
        Assert.True(wt.IsActive);
    }

    [Fact]
    public async Task Operation_gate_is_released_after_a_failed_commit()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();

        var first = await Assert.ThrowsAsync<CodingGitException>(() =>
            fx.Service.CommitAsync(wt, new CommitInput("nothing", null, CodingGitFixture.Model)));
        Assert.Equal(CodingGitError.NothingToCommit, first.Error);

        fx.Engine.StageChange(wt.Path, "a.txt", "+a");
        var id = await fx.Service.CommitAsync(wt, new CommitInput("now", null, CodingGitFixture.Model));
        Assert.StartsWith("fake", id.Sha, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_lease_is_taken_over_and_the_old_holder_is_told_it_lost_the_lease()
    {
        var time = new ManualTime();
        using var fx = new CodingGitFixture(time: time, options: new CodingGitOptions { LeaseDuration = TimeSpan.FromMinutes(10) });
        var crashed = await fx.OpenAsync(); // never disposed: the holder "crashed"

        time.Advance(TimeSpan.FromMinutes(11));
        await using var successor = await fx.OpenAsync();

        Assert.False(crashed.IsActive);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.DiffAgainstBaseAsync(crashed));
        Assert.Equal(CodingGitError.LeaseLost, ex.Error);
        Assert.True(successor.IsActive);
    }

    [Fact]
    public async Task Activity_renews_the_lease()
    {
        var time = new ManualTime();
        using var fx = new CodingGitFixture(time: time, options: new CodingGitOptions { LeaseDuration = TimeSpan.FromMinutes(10) });
        await using var wt = await fx.OpenAsync();

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(8));
            await fx.Service.DiffAgainstBaseAsync(wt);
        }

        Assert.True(wt.IsActive);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeBusy, ex.Error);
    }

    [Fact]
    public async Task Lock_admits_exactly_one_holder_under_concurrent_stress()
    {
        var locks = new WorktreeLocks();
        var inside = 0;
        var maxInside = 0;
        var acquired = 0;
        var refused = 0;

        async Task Worker()
        {
            for (var i = 0; i < 200; i++)
            {
                var lease = await locks.TryAcquireAsync("k", TimeSpan.FromMinutes(1), CancellationToken.None);
                if (lease is null)
                {
                    Interlocked.Increment(ref refused);
                    await Task.Yield();
                    continue;
                }

                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                Interlocked.Increment(ref acquired);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
                await lease.DisposeAsync();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(Worker)));

        Assert.Equal(1, maxInside);
        Assert.True(acquired > 0);
        Assert.Equal(32 * 200, acquired + refused);
        Assert.NotNull(await locks.TryAcquireAsync("k", TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task Service_stress_one_winner_per_worktree()
    {
        using var fx = new CodingGitFixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            try
            {
                return (CodingWorktree?)await fx.OpenAsync();
            }
            catch (CodingGitException ex) when (ex.Error == CodingGitError.WorktreeBusy)
            {
                return null;
            }
        })));

        Assert.Single(results, r => r is not null);
        await results.Single(r => r is not null)!.DisposeAsync();
    }

    [Fact]
    public async Task Lease_dispose_is_idempotent_and_does_not_free_a_successor()
    {
        var time = new ManualTime();
        var locks = new WorktreeLocks(time);
        var old = await locks.TryAcquireAsync("k", TimeSpan.FromMinutes(1), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        var next = await locks.TryAcquireAsync("k", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.False(old!.IsHeld);
        Assert.False(old.TryRenew());
        await old.DisposeAsync();
        Assert.True(next!.IsHeld);
        Assert.Null(await locks.TryAcquireAsync("k", TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task Di_helper_registers_a_working_service()
    {
        using var fx = new CodingGitFixture();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkShareLayout>(new FixedWorkShareLayout(fx.Share, fx.Passport));
        services.AddSingleton<ICodingGitEngine>(fx.Engine);
        services.AddSingleton<IHostPushPort>(fx.Port);
        services.AddSingleton<IPushGate>(fx.Gate);
        services.AddSingleton<IAgentGitIdentity>(new FakeAgentGitIdentity());
        services.AddBazaarCodingGit(o => o.LeaseDuration = TimeSpan.FromMinutes(5));

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<CodingGitService>();
        await using var wt = await service.CreateWorktreeAsync(CodingGitFixture.Request());
        Assert.True(wt.IsActive);
        Assert.IsType<WorktreeLocks>(provider.GetRequiredService<IWorktreeLocks>());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
