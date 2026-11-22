using Bazaar.Server.AgentSafety.Wake;
using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Tests;

/// <summary><see cref="KillSwitchWakeGate"/> over the real <see cref="KillSwitchEngine"/> with its in-memory store.</summary>
public sealed class KillSwitchWakeGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NoopAudit : IKillSwitchAuditSink
    {
        public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopContainment : IAgentContainment
    {
        public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
            Task.FromResult<ReleaseChallenge?>(null);

        public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task A_global_stop_and_an_agent_stop_deny_the_wake_and_a_resume_allows_it()
    {
        var engine = new KillSwitchEngine(new NoopAudit(), new NoopContainment());
        var gate = new KillSwitchWakeGate(engine);
        var agent = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        // The engine denies everything until its state has loaded; a resume waits for the load.
        await engine.ResumeGlobalAsync("owner", "load", Ct);

        Assert.True(gate.Check(agent).Allowed);

        await engine.EngageGlobalAsync("owner", "test stop", Ct);
        var denied = gate.Check(agent);
        Assert.False(denied.Allowed);
        Assert.Equal("test stop", denied.Reason);
        Assert.False(gate.Check(other).Allowed);

        await engine.ResumeGlobalAsync("owner", "test resume", Ct);
        Assert.True(gate.Check(agent).Allowed);

        await engine.EngageAgentAsync("owner", agent.ToString("D"), "agent stop", Ct);
        Assert.False(gate.Check(agent).Allowed);
        Assert.True(gate.Check(other).Allowed);
    }
}
