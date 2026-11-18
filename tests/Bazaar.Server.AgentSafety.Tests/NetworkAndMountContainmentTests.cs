using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class NetworkAndMountContainmentTests
{
    private sealed class Log
    {
        public List<string> Calls { get; } = [];
    }

    private sealed class FakeInner(Log log, ContainmentReleaseOutcome outcome = ContainmentReleaseOutcome.Released) : IAgentContainment
    {
        public Exception? ContainFails { get; set; }

        public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
        {
            log.Calls.Add("inner.contain");
            return ContainFails is null ? Task.CompletedTask : Task.FromException(ContainFails);
        }

        public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
            Task.FromResult<ReleaseChallenge?>(null);

        public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
        {
            log.Calls.Add("inner.release");
            return Task.FromResult(new ContainmentReleaseResult(outcome));
        }
    }

    private sealed class FakeEgress(Log log) : IEgressPolicy
    {
        public Exception? BlockFails { get; set; }
        public EgressApplyResult ReleaseResult { get; set; } = new(EgressApplyOutcome.Applied);

        public Task<EgressApplyResult> ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<EgressApplyResult> InitialiseAsync(string agentId, System.Net.IPAddress proxyAddress, int proxyPort, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task BlockAllAsync(string agentId, CancellationToken ct = default)
        {
            log.Calls.Add("egress.block");
            return BlockFails is null ? Task.CompletedTask : Task.FromException(BlockFails);
        }

        public Task<EgressApplyResult> ReleaseBlockAsync(string agentId, CancellationToken ct = default)
        {
            log.Calls.Add("egress.release");
            return Task.FromResult(ReleaseResult);
        }
    }

    private sealed class FakeMounts(Log log) : IMountManager
    {
        public Exception? DetachFails { get; set; }
        public List<(Guid Agent, DetachReason Reason)> Detached { get; } = [];

        public Task<AttachedMount> AttachAsync(MountSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task<DetachResult> DetachAsync(Guid mountId, DetachReason reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DetachResult>> DetachAllForAgentAsync(Guid agentId, DetachReason reason, CancellationToken ct)
        {
            log.Calls.Add("mounts.detach");
            Detached.Add((agentId, reason));
            return DetachFails is null ? Task.FromResult<IReadOnlyList<DetachResult>>([]) : Task.FromException<IReadOnlyList<DetachResult>>(DetachFails);
        }

        public Task<WriteBackResult> WriteBackProfileAsync(Guid mountId, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ReportCanaryTokenUseAsync(string tokenValue, string source, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RearmWatchesAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAudit(bool fails = false) : IAuditLog
    {
        public List<string> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e.Payload);
            return fails ? Task.FromException(new InvalidOperationException("audit down")) : Task.CompletedTask;
        }
    }

    private static ContainmentReleaseRequest Request(string agent) => new(agent, "device", new byte[16], DateTimeOffset.UtcNow, new byte[64]);

    private static (NetworkAndMountContainment Sut, Log Log, FakeInner Inner, FakeEgress Egress, FakeMounts Mounts, FakeAudit Audit) Build(
        ContainmentReleaseOutcome outcome = ContainmentReleaseOutcome.Released, bool auditFails = false)
    {
        var log = new Log();
        var inner = new FakeInner(log, outcome);
        var egress = new FakeEgress(log);
        var mounts = new FakeMounts(log);
        var audit = new FakeAudit(auditFails);
        return (new NetworkAndMountContainment(inner, egress, mounts, audit), log, inner, egress, mounts, audit);
    }

    [Fact]
    public async Task Contain_blocks_egress_detaches_mounts_for_the_kill_switch_then_runs_the_inner_containment()
    {
        var (sut, log, _, _, mounts, audit) = Build();
        var agent = Guid.NewGuid();

        await sut.ContainAsync(agent.ToString(), "owner", TestContext.Current.CancellationToken);

        Assert.Equal(["egress.block", "mounts.detach", "inner.contain"], log.Calls);
        Assert.Equal([(agent, DetachReason.KillSwitch)], mounts.Detached);
        Assert.Equal(3, audit.Entries.Count);
    }

    [Fact]
    public async Task Every_step_runs_when_earlier_ones_fail_and_the_failures_are_aggregated()
    {
        var (sut, log, inner, egress, mounts, audit) = Build();
        egress.BlockFails = new InvalidOperationException("egress down");
        mounts.DetachFails = new InvalidOperationException("mounts down");
        inner.ContainFails = new InvalidOperationException("vm down");

        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            sut.ContainAsync(Guid.NewGuid().ToString(), "owner", TestContext.Current.CancellationToken));

        Assert.Equal(["egress.block", "mounts.detach", "inner.contain"], log.Calls);
        Assert.Equal(3, ex.InnerExceptions.Count);
        Assert.Contains(audit.Entries, e => e.Contains("egress_blocked_failed"));
        Assert.Contains(audit.Entries, e => e.Contains("mounts_detached_failed"));
        Assert.Contains(audit.Entries, e => e.Contains("inner_contained_failed"));
    }

    [Fact]
    public async Task A_single_failure_is_rethrown_as_itself_after_the_other_steps_ran()
    {
        var (sut, log, _, egress, _, _) = Build();
        egress.BlockFails = new InvalidOperationException("egress down");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ContainAsync(Guid.NewGuid().ToString(), "owner", TestContext.Current.CancellationToken));

        Assert.Equal(["egress.block", "mounts.detach", "inner.contain"], log.Calls);
    }

    [Fact]
    public async Task A_non_guid_agent_id_fails_the_mount_step_but_still_blocks_egress_and_contains()
    {
        var (sut, log, _, _, mounts, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => sut.ContainAsync("not-a-guid", "owner", TestContext.Current.CancellationToken));

        Assert.Equal(["egress.block", "inner.contain"], log.Calls);
        Assert.Empty(mounts.Detached);
    }

    [Fact]
    public async Task Contain_is_idempotent_and_a_failing_audit_log_does_not_stop_the_steps()
    {
        var (sut, log, _, _, _, _) = Build(auditFails: true);
        var agent = Guid.NewGuid().ToString();

        await sut.ContainAsync(agent, "owner", TestContext.Current.CancellationToken);
        await sut.ContainAsync(agent, "owner", TestContext.Current.CancellationToken);

        Assert.Equal(["egress.block", "mounts.detach", "inner.contain", "egress.block", "mounts.detach", "inner.contain"], log.Calls);
    }

    [Theory]
    [InlineData(ContainmentReleaseOutcome.Released)]
    [InlineData(ContainmentReleaseOutcome.NotContained)]
    public async Task Egress_is_released_only_after_the_inner_release_verified_the_owner_request(ContainmentReleaseOutcome outcome)
    {
        var (sut, log, _, _, _, audit) = Build(outcome);
        var agent = Guid.NewGuid().ToString();

        var result = await sut.ReleaseAsync(agent, Request(agent), TestContext.Current.CancellationToken);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(["inner.release", "egress.release"], log.Calls);
        Assert.Contains(audit.Entries, e => e.Contains("egress_released"));
    }

    [Fact]
    public async Task A_denied_release_leaves_egress_blocked()
    {
        var (sut, log, _, _, _, _) = Build(ContainmentReleaseOutcome.Denied);
        var agent = Guid.NewGuid().ToString();

        var result = await sut.ReleaseAsync(agent, Request(agent), TestContext.Current.CancellationToken);

        Assert.Equal(ContainmentReleaseOutcome.Denied, result.Outcome);
        Assert.Equal(["inner.release"], log.Calls);
    }

    [Fact]
    public async Task A_release_that_leaves_the_egress_block_in_place_throws_and_is_audited()
    {
        var (sut, _, _, egress, _, audit) = Build();
        egress.ReleaseResult = new EgressApplyResult(EgressApplyOutcome.Blocked, "No approved rules to restore.");
        var agent = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReleaseAsync(agent, Request(agent), TestContext.Current.CancellationToken));

        Assert.Contains(audit.Entries, e => e.Contains("egress_release_refused"));
    }
}
