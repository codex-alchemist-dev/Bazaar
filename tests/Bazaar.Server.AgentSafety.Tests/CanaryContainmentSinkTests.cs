using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.AgentSafety.Tests;

public class CanaryContainmentSinkTests
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-00000000000a");

    private static CanaryUseReport Report(bool retired = false) =>
        new(_agent, Guid.NewGuid(), Guid.NewGuid(), "abcdef0123456789abcdef", "creds/canary.txt", CanaryUseKind.FileRead, "hyperv", DateTimeOffset.UnixEpoch, retired);

    private sealed class Audit : IKillSwitchAuditSink
    {
        public List<KillSwitchAuditEntry> Entries { get; } = [];

        public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class Containment : IAgentContainment
    {
        public List<string> Contained { get; } = [];

        public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
        {
            Contained.Add(agentId);
            return Task.CompletedTask;
        }

        public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
            Task.FromResult<ReleaseChallenge?>(null);

        public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class Ward(bool throws = false) : IWardTriggerIntake
    {
        public List<(Guid Agent, string Detail)> Canaries { get; } = [];

        public Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken ct)
        {
            Canaries.Add((agentId, detail));
            return throws ? throw new InvalidOperationException("ward down") : Task.FromResult(WardOutcome.Refuse("x", null));
        }

        public Task<WardOutcome?> ReportOutcomesAsync(Guid agentId, string tag, IReadOnlyList<bool> recent, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_canary_use_freezes_the_agent_pages_through_the_audit_record_and_tells_the_ward()
    {
        var audit = new Audit();
        var containment = new Containment();
        var engine = new KillSwitchEngine(audit, containment);
        var ward = new Ward();

        await new CanaryContainmentSink(engine, ward).ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken);

        Assert.False(engine.Evaluate(_agent.ToString("D"), ToolEffect.Read).Allowed);
        Assert.Equal([_agent.ToString("D")], containment.Contained);
        var entry = Assert.Single(audit.Entries);
        Assert.Contains("canary FileRead", entry.Reason);
        Assert.Contains("abcdef012345", entry.Reason);
        Assert.DoesNotContain("abcdef0123456789abcdef", entry.Reason);
        Assert.Equal(_agent, Assert.Single(ward.Canaries).Agent);
    }

    [Fact]
    public async Task A_failing_ward_does_not_undo_or_hide_the_freeze()
    {
        var engine = new KillSwitchEngine(new Audit(), new Containment());

        await new CanaryContainmentSink(engine, new Ward(throws: true)).ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken);

        Assert.False(engine.Evaluate(_agent.ToString("D"), ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task Without_a_ward_the_agent_is_still_frozen()
    {
        var engine = new KillSwitchEngine(new Audit(), new Containment());

        await new CanaryContainmentSink(engine).ReportCanaryUseAsync(Report(retired: true), TestContext.Current.CancellationToken);

        Assert.False(engine.Evaluate(_agent.ToString("D"), ToolEffect.Write).Allowed);
    }
}
