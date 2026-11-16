using Bazaar.Server.Host.Safety;
using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.Host.Tests.Safety;

public sealed class KillSwitchStartupGateTests
{
    /// <summary>A stop store that cannot be read, as when Postgres is down at start.</summary>
    private sealed class UnreadableStopStore : IKillSwitchStateStore
    {
        public Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task ClearGlobalAsync(CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default) => throw new InvalidOperationException("store down");
        public Task ClearAgentStopAsync(string agentId, CancellationToken ct = default) => throw new InvalidOperationException("store down");
    }

    /// <summary>A store that reads an empty state, as on a healthy first start.</summary>
    private sealed class EmptyStopStore : IKillSwitchStateStore
    {
        public Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default) => Task.FromResult(new KillSwitchStatus(null, Array.Empty<KillSwitchStop>()));
        public Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearGlobalAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAgentStopAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;
    }

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
    public async Task The_gate_refuses_to_start_when_the_stop_store_cannot_be_read()
    {
        var engine = new KillSwitchEngine(new NoopAudit(), new NoopContainment(), new UnreadableStopStore());
        var gate = new KillSwitchStartupGate(engine);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_gate_opens_once_the_stop_store_reads()
    {
        var engine = new KillSwitchEngine(new NoopAudit(), new NoopContainment(), new EmptyStopStore());
        var gate = new KillSwitchStartupGate(engine);

        await gate.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }
}
