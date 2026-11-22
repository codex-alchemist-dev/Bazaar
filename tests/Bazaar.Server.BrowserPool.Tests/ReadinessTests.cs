using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.BrowserPool.Tests;

public class ReadinessTests
{
    private sealed class FakeCheck(Func<Task<IReadOnlyList<PoolReadinessFailure>>> run) : IPoolReadinessCheck
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PoolReadinessFailure>> CheckAsync(CancellationToken ct)
        {
            Calls++;
            return run();
        }
    }

    private static FakeCheck Failing(params string[] ids) =>
        new(() => Task.FromResult<IReadOnlyList<PoolReadinessFailure>>(ids.Select(i => new PoolReadinessFailure(i, "reason " + i)).ToList()));

    private static FakeCheck Passing() => new(() => Task.FromResult<IReadOnlyList<PoolReadinessFailure>>([]));

    private static (BrowserPoolService Pool, Rig Rig) Make(params IPoolReadinessCheck[] checks)
    {
        var rig = new Rig();
        var pool = new BrowserPoolService(Options.Create(rig.Options), rig.Host, rig.Egress, rig.Gate, rig.Audit, rig.Time, checks);
        return (pool, rig);
    }

    private static Task<PoolSession> Open(BrowserPoolService pool) =>
        pool.OpenAsync(new PoolSessionRequest("agent-a", PoolDisplayMode.Headless, null), CancellationToken.None);

    [Fact]
    public async Task With_no_check_registered_a_session_opens()
    {
        var (pool, rig) = Make();
        using var _ = rig;
        using var __ = pool;

        Assert.NotNull(await Open(pool));
    }

    [Fact]
    public async Task Passing_checks_let_the_session_open()
    {
        var (pool, rig) = Make(Passing(), Passing());
        using var _ = rig;
        using var __ = pool;

        Assert.NotNull(await Open(pool));
    }

    [Fact]
    public async Task A_failing_check_refuses_and_lists_the_ids_and_starts_nothing()
    {
        var (pool, rig) = Make(Passing(), Failing("program:bwrap", "directory:slot-root"));
        using var _ = rig;
        using var __ = pool;

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => Open(pool));

        Assert.Equal(PoolFailure.ImageNotReady, ex.Failure);
        Assert.Contains("program:bwrap", ex.Message);
        Assert.Contains("directory:slot-root", ex.Message);
        Assert.DoesNotContain("reason", ex.Message);
        Assert.Empty(rig.Host.Ran);
        Assert.Empty(rig.Host.Started);
        var row = Assert.Single(rig.Audit.RefusedRows);
        Assert.Equal(PoolFailure.ImageNotReady, row.Failure);
    }

    [Fact]
    public async Task A_check_that_throws_refuses()
    {
        var (pool, rig) = Make(new FakeCheck(() => throw new InvalidOperationException("boom with detail")));
        using var _ = rig;
        using var __ = pool;

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => Open(pool));

        Assert.Equal(PoolFailure.ImageNotReady, ex.Failure);
        Assert.Contains("readiness-check-error", ex.Message);
        Assert.DoesNotContain("boom", ex.Message);
        Assert.Empty(rig.Host.Started);
    }

    [Fact]
    public async Task Every_check_is_asked_even_after_a_failure()
    {
        var first = Failing("a");
        var second = Failing("b");
        var (pool, rig) = Make(first, second);
        using var _ = rig;
        using var __ = pool;

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => Open(pool));

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Contains("a, b", ex.Message);
    }

    [Fact]
    public async Task A_fixed_image_is_accepted_on_the_next_open()
    {
        bool ready = false;
        var check = new FakeCheck(() => Task.FromResult<IReadOnlyList<PoolReadinessFailure>>(ready ? [] : [new PoolReadinessFailure("x", "y")]));
        var (pool, rig) = Make(check);
        using var _ = rig;
        using var __ = pool;

        await Assert.ThrowsAsync<BrowserPoolException>(() => Open(pool));
        ready = true;

        Assert.NotNull(await Open(pool));
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_refusal()
    {
        using var cts = new CancellationTokenSource();
        var check = new FakeCheck(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var rig = new Rig();
        using var _ = rig;
        using var pool = new BrowserPoolService(Options.Create(rig.Options), rig.Host, rig.Egress, rig.Gate, rig.Audit, rig.Time, [check]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.OpenAsync(new PoolSessionRequest("agent-a", PoolDisplayMode.Headless, null), cts.Token));
    }
}
