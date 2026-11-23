using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Tests;

public class AsyncGateTests
{
    private sealed class ScriptedAsyncGate : IAsyncPoolGate
    {
        public Func<string, CancellationToken, ValueTask<bool>> Answer { get; set; } = (_, _) => ValueTask.FromResult(false);

        public int Calls { get; private set; }

        public ValueTask<bool> IsBlockedAsync(string agentId, CancellationToken ct)
        {
            Calls++;
            return Answer(agentId, ct);
        }
    }

    private sealed class ThrowingSyncGate : IPoolGate
    {
        public bool IsBlocked(string agentId) => throw new InvalidOperationException("broken");
    }

    private static BrowserPoolService Build(Rig rig, IPoolGate sync, IAsyncPoolGate? async) =>
        new(Options.Create(rig.Options), rig.Host, rig.Egress, sync, rig.Audit, rig.Time, null, async);

    [Fact]
    public async Task The_async_gate_is_asked_and_the_sync_gate_is_not()
    {
        using var rig = new Rig();
        var asyncGate = new ScriptedAsyncGate();
        using var pool = Build(rig, new ThrowingSyncGate(), asyncGate);

        var session = await pool.OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken);

        Assert.Equal("agent-1", session.AgentId);
        Assert.Equal(2, asyncGate.Calls);
    }

    [Fact]
    public async Task A_blocking_async_gate_refuses_with_agent_blocked_even_when_the_sync_gate_allows()
    {
        using var rig = new Rig();
        var asyncGate = new ScriptedAsyncGate { Answer = (_, _) => ValueTask.FromResult(true) };
        using var pool = Build(rig, rig.Gate, asyncGate);

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Empty(rig.Host.Started);
        Assert.Equal(PoolFailure.AgentBlocked, Assert.Single(rig.Audit.RefusedRows).Failure);
    }

    [Fact]
    public async Task A_throwing_async_gate_counts_as_blocked()
    {
        using var rig = new Rig();
        var asyncGate = new ScriptedAsyncGate { Answer = (_, _) => throw new InvalidOperationException("reader down") };
        using var pool = Build(rig, rig.Gate, asyncGate);

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Empty(rig.Host.Started);
    }

    [Fact]
    public async Task A_throwing_sync_gate_counts_as_blocked()
    {
        using var rig = new Rig();
        using var pool = Build(rig, new ThrowingSyncGate(), null);

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
    }

    [Fact]
    public async Task Cancellation_while_the_async_gate_waits_propagates_and_is_not_a_refusal()
    {
        using var rig = new Rig();
        var asyncGate = new ScriptedAsyncGate { Answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return false; } };
        using var pool = Build(rig, rig.Gate, asyncGate);
        using var cts = new CancellationTokenSource();

        Task<PoolSession> open = pool.OpenAsync(new PoolSessionRequest("agent-1"), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Empty(rig.Audit.RefusedRows);
        Assert.Empty(rig.Host.Started);
    }

    [Fact]
    public async Task A_gate_that_flips_while_the_slot_is_prepared_stops_the_launch_and_cleans_up()
    {
        using var rig = new Rig();
        bool blocked = false;
        var asyncGate = new ScriptedAsyncGate { Answer = (_, _) => ValueTask.FromResult(blocked) };
        rig.Host.OnRun = (_, _, _) =>
        {
            blocked = true;
            return Task.CompletedTask;
        };
        using var pool = Build(rig, rig.Gate, asyncGate);

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Empty(rig.Host.Started);
        Assert.Equal(0, pool.Snapshot().Open + pool.Snapshot().Opening);
        Assert.Empty(pool.Snapshot().Quarantined);
    }

    [Fact]
    public async Task Closing_and_sweeping_do_not_consult_the_gate()
    {
        using var rig = new Rig();
        var asyncGate = new ScriptedAsyncGate();
        using var pool = Build(rig, new ThrowingSyncGate(), asyncGate);
        var session = await pool.OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken);
        int calls = asyncGate.Calls;
        asyncGate.Answer = (_, _) => ValueTask.FromResult(true);

        Assert.Equal(1, await pool.CloseAllForAgentAsync("agent-1", TestContext.Current.CancellationToken));
        await pool.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(calls, asyncGate.Calls);
        Assert.Empty(pool.Sessions(session.AgentId));
    }
}
