using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.KillSwitch;

public class KillSwitchReaderTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryKillSwitchAuditSink _audit = new();
    private readonly RecordingContainment _containment = new();

    private KillSwitchEngine Engine(IKillSwitchStateStore? store = null) =>
        new(_audit, _containment, store ?? new InMemoryKillSwitchStateStore(), _time);

    [Fact]
    public async Task An_agent_with_no_stop_is_not_frozen()
    {
        var reader = Engine();
        await reader.WaitUntilStateLoadedAsync(TestContext.Current.CancellationToken);

        Assert.False(await reader.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_stop_freezes_that_agent_only()
    {
        var engine = Engine();
        await engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", TestContext.Current.CancellationToken);
        var reader = engine;

        Assert.True(await reader.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));
        Assert.False(await reader.IsFrozenAsync("agent-b", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_global_stop_freezes_every_agent()
    {
        var engine = Engine();
        await engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        var reader = engine;

        Assert.True(await reader.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));
        Assert.True(await reader.IsFrozenAsync("agent-z", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resuming_the_global_stop_unfreezes_agents()
    {
        var engine = Engine();
        await engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        await engine.ResumeGlobalAsync("owner-1", "Clear", TestContext.Current.CancellationToken);

        Assert.False(await engine.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_agent_is_frozen_until_the_state_is_loaded()
    {
        var load = new TaskCompletionSource();
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { LoadGate = load.Task };
        var reader = Engine(store);

        Assert.True(await reader.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));

        load.SetResult();
        await reader.WaitUntilStateLoadedAsync(TestContext.Current.CancellationToken);
        Assert.False(await reader.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_load_keeps_every_agent_frozen()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailLoads = true };
        var reader = Engine(store);

        Assert.True(await reader.IsFrozenAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ToolEffect.Read, true)]
    [InlineData(ToolEffect.Write, false)]
    [InlineData(ToolEffect.Egress, false)]
    public async Task EvaluateAsync_returns_the_engine_decision_under_a_global_stop(ToolEffect effect, bool allowed)
    {
        var engine = Engine();
        await engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);

        var decision = await engine.EvaluateAsync("agent-a", effect, TestContext.Current.CancellationToken);

        Assert.Equal(engine.Evaluate("agent-a", effect), decision);
        Assert.Equal(allowed, decision.Allowed);
    }

    [Fact]
    public async Task EvaluateAsync_denies_every_effect_for_a_stopped_agent()
    {
        var engine = Engine();
        await engine.EngageAgentAsync("owner-1", "agent-a", "Breach", TestContext.Current.CancellationToken);
        var reader = engine;

        var decision = await reader.EvaluateAsync("agent-a", ToolEffect.Read, TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal(KillSwitchScope.Agent, decision.Scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_agent_id_is_rejected(string agentId)
    {
        var reader = Engine();

        await Assert.ThrowsAsync<ArgumentException>(async () => await reader.IsFrozenAsync(agentId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await reader.EvaluateAsync(agentId, ToolEffect.Read, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_read_throws()
    {
        var reader = Engine();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.IsFrozenAsync("agent-a", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.EvaluateAsync("agent-a", ToolEffect.Read, cts.Token));
    }

    [Fact]
    public void AddAgentSafety_registers_the_engine_as_the_kill_switch_reader()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IKillSwitchAuditSink>(_audit);
        services.AddSingleton<IAgentContainment>(_containment);
        services.AddAgentSafety(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<KillSwitchEngine>(), provider.GetRequiredService<IKillSwitchReader>());
    }
}
