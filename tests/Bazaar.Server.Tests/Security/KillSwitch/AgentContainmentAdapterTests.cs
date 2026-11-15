using System.Text.Json.Nodes;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>Pauser that appends to a shared order list, so tests can check the call order and inject a failure.</summary>
internal sealed class OrderedVmPauser(List<string> order) : IVmPauser
{
    /// <summary>When set, <see cref="PauseAsync"/> throws, to test a failing pause.</summary>
    public bool Fail { get; set; }

    /// <summary>When set, <see cref="ResumeAsync"/> throws, to test a failing VM resume.</summary>
    public bool FailResume { get; set; }

    public Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("pause:" + agentId);
        return Fail ? Task.FromException(new InvalidOperationException("pause failed")) : Task.CompletedTask;
    }

    public Task ResumeAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("vm-resume:" + agentId);
        return FailResume ? Task.FromException(new InvalidOperationException("vm resume failed")) : Task.CompletedTask;
    }
}

/// <summary>Revoker that appends to a shared order list, so tests can check the call order.</summary>
internal sealed class OrderedTokenRevoker(List<string> order) : IAgentTokenRevoker
{
    public Task RevokeAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("revoke:" + agentId);
        return Task.CompletedTask;
    }

    public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("token-resume:" + agentId);
        return Task.FromResult(TokenResumeResult.NotReversible);
    }
}

public class AgentContainmentAdapterTests
{
    private static readonly string[] _containOrder = ["revoke:agent-a", "pause:agent-a"];

    private readonly List<string> _order = [];
    private readonly OrderedVmPauser _pauser;
    private readonly OrderedTokenRevoker _revoker;
    private readonly RecordingAuditLog _log = new();
    private readonly AgentContainmentAdapter _adapter;

    public AgentContainmentAdapterTests()
    {
        _pauser = new OrderedVmPauser(_order);
        _revoker = new OrderedTokenRevoker(_order);
        var owners = new InMemoryOwnerKeyRegistry();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", [1]));
        owners.Register(new OwnerDeviceKey("owner-1", "dev-revoked", [2], Revoked: true));
        _adapter = new AgentContainmentAdapter(_pauser, _revoker, owners, _log);
    }

    [Fact]
    public async Task Contain_revokes_tokens_then_pauses_the_vms_once_each()
    {
        await _adapter.ContainAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Equal(_containOrder, _order);
    }

    [Fact]
    public async Task Contain_is_safe_to_repeat()
    {
        await _adapter.ContainAsync("agent-a", TestContext.Current.CancellationToken);
        await _adapter.ContainAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Equal(4, _order.Count);
    }

    [Fact]
    public async Task A_failing_pause_surfaces_after_the_tokens_are_revoked()
    {
        _pauser.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _adapter.ContainAsync("agent-a", TestContext.Current.CancellationToken));

        Assert.Equal(_containOrder, _order);
    }

    private static string Step(AuditEntry e) => JsonNode.Parse(e.Payload)!["step"]!.GetValue<string>();

    [Fact]
    public async Task Owner_release_resumes_vms_then_tokens_with_one_audit_record_per_step()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", ct);
        _order.Clear();

        var result = await _adapter.ReleaseAsync("agent-a", "dev-owner", ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.False(result.TokensRestored);
        Assert.Equal(["vm-resume:agent-a", "token-resume:agent-a"], _order);
        Assert.Equal(["vm_resumed", "tokens_not_reversible"], _log.Entries.Select(Step));
        Assert.All(_log.Entries, e =>
        {
            Assert.Equal(AgentContainmentAdapter.ReleaseKind, e.Kind);
            Assert.Equal("owner-1", e.Actor);
        });
    }

    [Theory]
    [InlineData("dev-unknown")]
    [InlineData("dev-revoked")]
    [InlineData("agent-a")]
    public async Task Non_owner_release_is_denied_resumes_nothing_and_is_audited_once(string caller)
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", ct);
        _order.Clear();

        var result = await _adapter.ReleaseAsync("agent-a", caller, ct);

        Assert.Equal(ContainmentReleaseOutcome.Denied, result.Outcome);
        Assert.Empty(_order);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal("denied", Step(entry));
        Assert.Equal(caller, entry.Actor);
    }

    [Fact]
    public async Task A_denied_release_leaves_the_agent_releasable_by_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", ct);
        await _adapter.ReleaseAsync("agent-a", "dev-unknown", ct);

        var result = await _adapter.ReleaseAsync("agent-a", "dev-owner", ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
    }

    [Fact]
    public async Task Releasing_an_agent_that_was_never_contained_is_a_no_op_with_one_audit_record()
    {
        var result = await _adapter.ReleaseAsync("agent-a", "dev-owner", TestContext.Current.CancellationToken);

        Assert.Equal(ContainmentReleaseOutcome.NotContained, result.Outcome);
        Assert.Empty(_order);
        Assert.Equal("not_contained", Step(Assert.Single(_log.Entries)));
    }

    [Fact]
    public async Task A_failing_vm_resume_stops_the_token_resume_and_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", ct);
        _order.Clear();
        _pauser.FailResume = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _adapter.ReleaseAsync("agent-a", "dev-owner", ct));

        Assert.Equal(["vm-resume:agent-a"], _order);
        Assert.Equal("vm_resume_failed", Step(Assert.Single(_log.Entries)));

        // The agent is still contained, so a retry runs again.
        _pauser.FailResume = false;
        var retry = await _adapter.ReleaseAsync("agent-a", "dev-owner", ct);
        Assert.Equal(ContainmentReleaseOutcome.Released, retry.Outcome);
    }

    [Fact]
    public async Task A_second_release_after_a_completed_release_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", ct);
        await _adapter.ReleaseAsync("agent-a", "dev-owner", ct);
        _order.Clear();

        var result = await _adapter.ReleaseAsync("agent-a", "dev-owner", ct);

        Assert.Equal(ContainmentReleaseOutcome.NotContained, result.Outcome);
        Assert.Empty(_order);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Contain_and_release_reject_a_blank_agent_id(string agentId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ContainAsync(agentId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ReleaseAsync(agentId, "dev-owner", TestContext.Current.CancellationToken));
        Assert.Empty(_order);
    }

    [Fact]
    public async Task Release_rejects_a_blank_owner_actor_id()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ReleaseAsync("agent-a", " ", TestContext.Current.CancellationToken));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task An_agent_stop_through_the_engine_contains_the_agent_once()
    {
        var engine = new KillSwitchEngine(
            new KillSwitchAuditAdapter(_log),
            _adapter,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)));

        await engine.EngageAgentAsync("owner-1", "agent-a", "Suspicious output", TestContext.Current.CancellationToken);

        Assert.Equal(_containOrder, _order);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task An_engine_resume_by_a_non_owner_is_refused_and_the_agent_stays_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new KillSwitchEngine(new KillSwitchAuditAdapter(_log), _adapter, new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)));
        await engine.EngageAgentAsync("owner-1", "agent-a", "Stop", ct);
        _order.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.ResumeAgentAsync("dev-unknown", "agent-a", "Back", ct));

        Assert.Empty(_order);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task An_engine_resume_by_the_owner_releases_and_lifts_the_stop()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new KillSwitchEngine(new KillSwitchAuditAdapter(_log), _adapter, new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)));
        await engine.EngageAgentAsync("owner-1", "agent-a", "Stop", ct);
        _order.Clear();

        await engine.ResumeAgentAsync("dev-owner", "agent-a", "Back", ct);

        Assert.Equal(["vm-resume:agent-a", "token-resume:agent-a"], _order);
        Assert.True(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }
}
