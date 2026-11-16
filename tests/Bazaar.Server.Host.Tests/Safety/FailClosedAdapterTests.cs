using Bazaar.Server.Host.Safety;
using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Host.Tests.Safety;

public sealed class FailClosedAdapterTests
{
    [Fact]
    public async Task Pause_without_a_vm_backend_throws_and_never_reports_success()
    {
        var pauser = new UnavailableVmPauser();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pauser.PauseAsync("agent-a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pauser.ResumeAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revoke_without_a_revoker_throws_so_containment_never_claims_tokens_were_revoked()
    {
        var revoker = new UnavailableTokenRevoker();

        await Assert.ThrowsAsync<InvalidOperationException>(() => revoker.RevokeAsync("agent-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Token_resume_without_a_revoker_reports_not_reversible_and_issues_nothing()
    {
        var revoker = new UnavailableTokenRevoker();

        var result = await revoker.ResumeAsync("agent-a", TestContext.Current.CancellationToken);

        Assert.Equal(TokenResumeOutcome.NotReversible, result.Outcome);
    }
}
