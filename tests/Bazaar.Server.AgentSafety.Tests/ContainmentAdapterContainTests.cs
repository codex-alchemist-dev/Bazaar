using Bazaar.Approvals;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.AgentSafety.Tests;

/// <summary>A contain runs both of its steps: a failing lease store must not leave the agent's VMs running.</summary>
public sealed class ContainmentAdapterContainTests
{
    private sealed class Revoker(bool fail) : IAgentTokenRevoker
    {
        public int Calls { get; private set; }

        public Task RevokeAsync(string agentId, CancellationToken ct = default)
        {
            Calls++;
            return fail ? throw new InvalidOperationException("lease store down") : Task.CompletedTask;
        }

        public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) => Task.FromResult(TokenResumeResult.NotReversible);
    }

    private sealed class Pauser(bool fail) : IVmPauser
    {
        public int Calls { get; private set; }

        public Task PauseAsync(string agentId, CancellationToken ct = default)
        {
            Calls++;
            return fail ? throw new TimeoutException("backend busy") : Task.CompletedTask;
        }

        public Task ResumeAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static AgentContainmentAdapter Adapter(IVmPauser pauser, IAgentTokenRevoker revoker, IContainmentStateStore store) =>
        new(pauser, revoker, new InMemoryOwnerKeyRegistry(), new ListAuditLog(), new InMemoryNonceStore(TimeProvider.System), store);

    [Fact]
    public async Task A_failed_revoke_still_pauses_the_vms_and_is_the_error_reported()
    {
        var pauser = new Pauser(fail: false);
        var store = new InMemoryContainmentStateStore();
        var adapter = Adapter(pauser, new Revoker(fail: true), store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ContainAsync("agent-a", "anomaly:canary_hit", TestContext.Current.CancellationToken));

        Assert.Equal("lease store down", error.Message);
        Assert.Equal(1, pauser.Calls);
        Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_both_steps_fail_both_errors_are_reported()
    {
        var revoker = new Revoker(fail: true);
        var pauser = new Pauser(fail: true);
        var adapter = Adapter(pauser, revoker, new InMemoryContainmentStateStore());

        var error = await Assert.ThrowsAsync<AggregateException>(() => adapter.ContainAsync("agent-a", "owner", TestContext.Current.CancellationToken));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(1, revoker.Calls);
        Assert.Equal(1, pauser.Calls);
    }

    [Fact]
    public async Task A_failed_pause_alone_is_reported_as_it_is()
    {
        var adapter = Adapter(new Pauser(fail: true), new Revoker(fail: false), new InMemoryContainmentStateStore());

        await Assert.ThrowsAsync<TimeoutException>(() => adapter.ContainAsync("agent-a", "owner", TestContext.Current.CancellationToken));
    }
}
