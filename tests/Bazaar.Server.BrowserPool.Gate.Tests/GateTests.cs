using Bazaar.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Gate.Tests;

public class GateTests
{
    private sealed class ScriptedReader : IKillSwitchReader
    {
        public Func<string, CancellationToken, ValueTask<bool>> Frozen { get; set; } = (_, _) => ValueTask.FromResult(false);

        public List<string> Asked { get; } = [];

        public ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken ct = default)
        {
            Asked.Add(agentId);
            return Frozen(agentId, ct);
        }

        public ValueTask<KillSwitchDecision> EvaluateAsync(string agentId, ToolEffect effect, CancellationToken ct = default) =>
            throw new InvalidOperationException("The pool gate must only ask IsFrozenAsync.");
    }

    private static KillSwitchPoolGate Gate(IKillSwitchReader? reader, FakeTimeProvider? time = null, int timeoutSeconds = 5) =>
        new(reader, time ?? new FakeTimeProvider(), new PoolGateOptions { ReadTimeoutSeconds = timeoutSeconds });

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public async Task An_agent_that_is_not_frozen_is_not_blocked()
    {
        var reader = new ScriptedReader();
        var gate = Gate(reader);

        Assert.False(await gate.IsBlockedAsync("agent-1", TestContext.Current.CancellationToken));
        Assert.False(gate.IsBlocked("agent-1"));
        Assert.Equal(["agent-1", "agent-1"], reader.Asked);
    }

    [Fact]
    public async Task A_frozen_agent_is_blocked()
    {
        var reader = new ScriptedReader { Frozen = (id, _) => ValueTask.FromResult(id == "agent-1") };
        var gate = Gate(reader);

        Assert.True(await gate.IsBlockedAsync("agent-1", TestContext.Current.CancellationToken));
        Assert.True(gate.IsBlocked("agent-1"));
        Assert.False(await gate.IsBlockedAsync("agent-2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_reader_blocks_everyone()
    {
        var gate = Gate(null);

        Assert.True(await gate.IsBlockedAsync("agent-1", TestContext.Current.CancellationToken));
        Assert.True(gate.IsBlocked("agent-1"));
    }

    [Fact]
    public async Task A_reader_that_throws_blocks()
    {
        var reader = new ScriptedReader { Frozen = (_, _) => throw new InvalidOperationException("down") };
        var gate = Gate(reader);

        Assert.True(await gate.IsBlockedAsync("agent-1", TestContext.Current.CancellationToken));
        Assert.True(gate.IsBlocked("agent-1"));
    }

    [Fact]
    public async Task A_reader_that_faults_later_blocks()
    {
        var reader = new ScriptedReader { Frozen = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("down"); } };
        var gate = Gate(reader);

        Assert.True(await gate.IsBlockedAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_agent_id_is_blocked_without_asking_the_reader(string agentId)
    {
        var reader = new ScriptedReader();
        var gate = Gate(reader);

        Assert.True(await gate.IsBlockedAsync(agentId, TestContext.Current.CancellationToken));
        Assert.True(gate.IsBlocked(agentId));
        Assert.Empty(reader.Asked);
    }

    [Fact]
    public async Task A_read_that_does_not_finish_in_time_blocks()
    {
        var time = new FakeTimeProvider();
        var reader = new ScriptedReader { Frozen = (_, _) => new ValueTask<bool>(new TaskCompletionSource<bool>().Task) };
        var gate = Gate(reader, time, timeoutSeconds: 3);

        ValueTask<bool> pending = gate.IsBlockedAsync("agent-1", TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await pending);
    }

    [Fact]
    public async Task The_caller_cancelling_propagates_and_is_not_a_verdict()
    {
        var reader = new ScriptedReader { Frozen = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return false; } };
        var gate = Gate(reader);
        using var cts = new CancellationTokenSource();

        ValueTask<bool> pending = gate.IsBlockedAsync("agent-1", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.IsBlockedAsync("agent-1", cts.Token));
    }

    [Fact]
    public void The_sync_member_never_waits_for_an_unfinished_read()
    {
        var reader = new ScriptedReader { Frozen = (_, _) => new ValueTask<bool>(new TaskCompletionSource<bool>().Task) };
        var gate = Gate(reader);

        Assert.True(gate.IsBlocked("agent-1"));
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Equal(5, PoolGateOptions.Bind(Config()).ReadTimeoutSeconds);
        Assert.Equal(9, PoolGateOptions.Bind(Config(("Bazaar:BrowserPoolGate:ReadTimeoutSeconds", "9"))).ReadTimeoutSeconds);
        Assert.Throws<InvalidOperationException>(() => PoolGateOptions.Bind(Config(("Bazaar:BrowserPoolGate:ReadTimeoutSeconds", "0"))));
        Assert.Throws<InvalidOperationException>(() => PoolGateOptions.Bind(Config(("Bazaar:BrowserPoolGate:ReadTimeoutSeconds", "61"))));
        Assert.Throws<InvalidOperationException>(() => PoolGateOptions.Bind(Config(("Bazaar:BrowserPoolGate:ReadTimeoutSeconds", "x"))));
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new BrowserPoolGateComponent().Manifest;

        Assert.Equal("browser-pool-gate", m.Id);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["browser-pool"], m.DependsOn);
        Assert.Equal(["browser-pool:gate"], m.Provides);
    }

    private static ServiceProvider Build(bool gateFirst, Action<IServiceCollection>? host = null)
    {
        var services = new ServiceCollection();
        IConfiguration config = Config();
        if (gateFirst)
        {
            new BrowserPoolGateComponent().AddServices(services, config);
            new BrowserPoolComponent().AddServices(services, config);
        }
        else
        {
            new BrowserPoolComponent().AddServices(services, config);
            new BrowserPoolGateComponent().AddServices(services, config);
        }

        host?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_gate_replaces_the_default_for_both_ports_in_either_order(bool gateFirst)
    {
        using ServiceProvider sp = Build(gateFirst, s => s.AddSingleton<IKillSwitchReader>(new ScriptedReader()));

        Assert.IsType<KillSwitchPoolGate>(sp.GetRequiredService<IPoolGate>());
        Assert.Same(sp.GetRequiredService<IPoolGate>(), sp.GetRequiredService<IAsyncPoolGate>());
        Assert.Single(sp.GetServices<IPoolGate>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_a_kill_switch_reader_every_open_is_refused(bool gateFirst)
    {
        using ServiceProvider sp = Build(gateFirst);

        Assert.True(sp.GetRequiredService<IPoolGate>().IsBlocked("agent-1"));
        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() =>
            sp.GetRequiredService<IBrowserPool>().OpenAsync(new PoolSessionRequest("agent-1"), TestContext.Current.CancellationToken));
        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
    }

    [Fact]
    public async Task A_frozen_agent_is_refused_by_the_pool_and_a_free_one_passes_the_gate()
    {
        var reader = new ScriptedReader { Frozen = (id, _) => ValueTask.FromResult(id == "agent-frozen") };
        using ServiceProvider sp = Build(true, s => s.AddSingleton<IKillSwitchReader>(reader));
        IBrowserPool pool = sp.GetRequiredService<IBrowserPool>();

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-frozen"), TestContext.Current.CancellationToken));
        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);

        // The pool is off by default; the free agent passes the gate and is stopped by the next check, never as AgentBlocked.
        var other = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-free"), TestContext.Current.CancellationToken));
        Assert.NotEqual(PoolFailure.AgentBlocked, other.Failure);
    }

    [Fact]
    public void A_later_host_registration_still_wins()
    {
        var custom = new CustomGate();
        using ServiceProvider sp = Build(false, s => s.AddSingleton<IPoolGate>(custom));

        Assert.Same(custom, sp.GetRequiredService<IPoolGate>());
    }

    private sealed class CustomGate : IPoolGate
    {
        public bool IsBlocked(string agentId) => false;
    }
}
