using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>Pauser that appends to a shared order list, so tests can check the call order and inject a failure.</summary>
internal sealed class OrderedVmPauser(List<string> order) : IVmPauser
{
    /// <summary>When set, <see cref="PauseAsync"/> throws, to test a failing pause.</summary>
    public bool Fail { get; set; }

    public Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("pause:" + agentId);
        return Fail ? Task.FromException(new InvalidOperationException("pause failed")) : Task.CompletedTask;
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
}

public class AgentContainmentAdapterTests
{
    private static readonly string[] _containOrder = ["revoke:agent-a", "pause:agent-a"];

    private readonly List<string> _order = [];
    private readonly OrderedVmPauser _pauser;
    private readonly OrderedTokenRevoker _revoker;
    private readonly AgentContainmentAdapter _adapter;

    public AgentContainmentAdapterTests()
    {
        _pauser = new OrderedVmPauser(_order);
        _revoker = new OrderedTokenRevoker(_order);
        _adapter = new AgentContainmentAdapter(_pauser, _revoker);
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

    [Fact]
    public async Task Release_calls_neither_the_pauser_nor_the_revoker()
    {
        await _adapter.ReleaseAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Empty(_order);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Contain_and_release_reject_a_blank_agent_id(string agentId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ContainAsync(agentId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ReleaseAsync(agentId, TestContext.Current.CancellationToken));
        Assert.Empty(_order);
    }

    [Fact]
    public async Task An_agent_stop_through_the_engine_contains_the_agent_once()
    {
        var engine = new KillSwitchEngine(
            new KillSwitchAuditAdapter(new RecordingAuditLog()),
            _adapter,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)));

        await engine.EngageAgentAsync("owner-1", "agent-a", "Suspicious output", TestContext.Current.CancellationToken);

        Assert.Equal(_containOrder, _order);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }
}
