using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class LeaseTokenRevokerTests
{
    [Fact]
    public async Task Revoking_an_agent_clears_its_active_leases()
    {
        var leases = new InMemoryCredentialLeaseStore(TimeProvider.System);
        var revoker = new LeaseTokenRevoker(leases);
        var issued = await leases.IssueAsync(
            new LeaseId(Guid.NewGuid()), "agent-a", new CredentialId(Guid.NewGuid()), "read", DateTimeOffset.UtcNow.AddHours(1), approvalId: null, TestContext.Current.CancellationToken);
        Assert.NotNull(issued);

        await revoker.RevokeAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Empty(await leases.ListActiveAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Token_resume_reports_not_reversible()
    {
        var revoker = new LeaseTokenRevoker(new InMemoryCredentialLeaseStore(TimeProvider.System));

        var result = await revoker.ResumeAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Equal(TokenResumeOutcome.NotReversible, result.Outcome);
    }
}
