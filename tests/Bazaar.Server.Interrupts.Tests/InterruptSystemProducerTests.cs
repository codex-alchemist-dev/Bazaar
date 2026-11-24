using Bazaar.Server.Agents.Data;
using Bazaar.Server.Health;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Interrupts.Tests;

public sealed class InterruptSystemProducerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _a = Guid.Parse("0196a6f0-0000-7000-8000-00000000000a");
    private static readonly Guid _b = Guid.Parse("0196a6f0-0000-7000-8000-00000000000b");
    private static readonly Guid _gcA = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _gcB = Guid.Parse("0196a6f0-0000-7000-8000-0000000000b1");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Push(Guid AgentId, InterruptSource Source, string Reason, Guid GcId);

    private sealed class FakeStack : IInterruptStackService
    {
        public List<Push> Pushes { get; } = [];
        public Func<Guid, Exception?> Fail { get; set; } = _ => null;

        public Task<InterruptFrame> PushAsync(Guid agentId, Guid authorId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt = null, bool emergency = false, CancellationToken ct = default)
        {
            if (Fail(agentId) is { } ex)
                throw ex;
            Pushes.Add(new Push(agentId, source, reason, gcId));
            return Task.FromResult(new InterruptFrame(Guid.NewGuid(), agentId, null, source, reason, gcId, InterruptFrameState.Active, _now, null, 1));
        }

        public Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct = default) => Task.FromResult<InterruptFrame?>(null);

        public Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InterruptFrame>>([]);
    }

    private sealed class Targets(InterruptSystemTargets value) : IInterruptSystemTargetsReader
    {
        public Task<InterruptSystemTargets> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    }

    private sealed class Agents(params Guid[] ids) : IAgentDirectoryReader
    {
        public Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentListing>>([.. ids.Select(i => new AgentListing(i, i.ToString("N")))]);
    }

    private sealed class Gcs(Dictionary<Guid, Guid> map) : IInterruptGcResolver
    {
        public Task<Guid?> ResolveAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(map.TryGetValue(agentId, out var gc) ? gc : (Guid?)null);
    }

    private static (InterruptSystemProducer Producer, FakeStack Stack) Make(InterruptSystemTargets mode, params Guid[] agents)
    {
        var stack = new FakeStack();
        var gcs = new Gcs(new() { [_a] = _gcA, [_b] = _gcB });
        return (new InterruptSystemProducer(stack, new Targets(mode), new Agents(agents), gcs), stack);
    }

    private static readonly BuildBroken _broken = new("proj-1", "run-42", _now);

    [Fact]
    public async Task None_pushes_nothing_for_either_signal()
    {
        var (producer, stack) = Make(InterruptSystemTargets.None, _a, _b);

        await producer.OnBuildBrokenAsync(_broken, Ct);
        await producer.OnTaintRaisedAsync(new TaintRaised(_a.ToString("D"), Guid.NewGuid(), TaintState.Hard, _now), Ct);

        Assert.Empty(stack.Pushes);
    }

    [Fact]
    public async Task Missing_settings_reader_means_none()
    {
        var stack = new FakeStack();
        var producer = new InterruptSystemProducer(stack, null, new Agents(_a), new Gcs(new() { [_a] = _gcA }));

        await producer.OnBuildBrokenAsync(_broken, Ct);

        Assert.Empty(stack.Pushes);
    }

    [Fact]
    public async Task Build_broken_pushes_a_system_build_frame_to_every_agent_in_its_working_gc()
    {
        var (producer, stack) = Make(InterruptSystemTargets.AllAgents, _a, _b);

        await producer.OnBuildBrokenAsync(_broken, Ct);

        Assert.Equal(2, stack.Pushes.Count);
        Assert.All(stack.Pushes, p =>
        {
            Assert.Equal(InterruptSource.SystemBuild, p.Source);
            Assert.Equal("Build broken in project proj-1 (run run-42)", p.Reason);
        });
        Assert.Equal(_gcA, stack.Pushes.Single(p => p.AgentId == _a).GcId);
        Assert.Equal(_gcB, stack.Pushes.Single(p => p.AgentId == _b).GcId);
    }

    [Fact]
    public async Task Hard_taint_pushes_a_system_security_frame_only_to_the_tainted_agent()
    {
        var (producer, stack) = Make(InterruptSystemTargets.AllAgents, _a, _b);

        await producer.OnTaintRaisedAsync(new TaintRaised(_b.ToString("D"), Guid.NewGuid(), TaintState.Hard, _now), Ct);

        var push = Assert.Single(stack.Pushes);
        Assert.Equal(_b, push.AgentId);
        Assert.Equal(InterruptSource.SystemSecurity, push.Source);
        Assert.Equal("Session taint raised to hard", push.Reason);
    }

    [Fact]
    public async Task Soft_taint_and_an_agent_that_is_not_active_push_nothing()
    {
        var (producer, stack) = Make(InterruptSystemTargets.AllAgents, _a);

        await producer.OnTaintRaisedAsync(new TaintRaised(_a.ToString("D"), Guid.NewGuid(), TaintState.Soft, _now), Ct);
        await producer.OnTaintRaisedAsync(new TaintRaised(_b.ToString("D"), Guid.NewGuid(), TaintState.Hard, _now), Ct);
        await producer.OnTaintRaisedAsync(new TaintRaised("not-a-guid", Guid.NewGuid(), TaintState.Hard, _now), Ct);

        Assert.Empty(stack.Pushes);
    }

    [Fact]
    public async Task Project_members_pushes_nobody_until_a_membership_source_exists()
    {
        var (producer, stack) = Make(InterruptSystemTargets.ProjectMembers, _a, _b);

        await producer.OnBuildBrokenAsync(_broken, Ct);
        await producer.OnTaintRaisedAsync(new TaintRaised(_a.ToString("D"), Guid.NewGuid(), TaintState.Hard, _now), Ct);

        Assert.Empty(stack.Pushes);
    }

    [Fact]
    public async Task An_agent_with_no_working_gc_is_skipped_and_a_failed_push_does_not_stop_the_rest()
    {
        var stack = new FakeStack { Fail = id => id == _a ? new InvalidOperationException("stack full") : null };
        var producer = new InterruptSystemProducer(stack, new Targets(InterruptSystemTargets.AllAgents), new Agents(_a, _b, Guid.NewGuid()),
            new Gcs(new() { [_a] = _gcA, [_b] = _gcB }));

        await producer.OnBuildBrokenAsync(_broken, Ct);

        Assert.Equal(_b, Assert.Single(stack.Pushes).AgentId);
    }

    [Fact]
    public async Task A_long_run_reference_is_clipped()
    {
        var (producer, stack) = Make(InterruptSystemTargets.AllAgents, _a);

        await producer.OnBuildBrokenAsync(new BuildBroken("p", new string('x', 500), _now), Ct);

        Assert.True(Assert.Single(stack.Pushes).Reason.Length < 120);
    }

    [Fact]
    public void The_target_setting_defaults_to_none_and_is_an_operator_install_key()
    {
        var key = InterruptSettings.SystemTargets;

        Assert.Equal("interrupt.system_targets", key.Name);
        Assert.Equal(InterruptSystemTargets.None, key.Default);
        Assert.Equal(SettingScope.Install, key.Scope);
        Assert.Equal(SettingRole.Operator, key.WhoMayChange);
        Assert.Contains(key, new InterruptSettingContribution().Settings);
    }

    [Fact]
    public async Task The_component_registers_one_producer_for_both_seams_and_the_setting()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInterruptStackService>(new FakeStack());
        new InterruptsComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var build = Assert.Single(provider.GetServices<IBuildBrokenListener>());
        var taint = Assert.Single(provider.GetServices<ITaintRaisedListener>());
        Assert.Same(build, taint);
        Assert.Contains(provider.GetServices<ISettingContribution>(), c => c is InterruptSettingContribution);
        await build.OnBuildBrokenAsync(_broken, Ct); // No targets reader registered: a no-op, not a failure.
    }
}
