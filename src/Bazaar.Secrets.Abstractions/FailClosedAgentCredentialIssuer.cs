namespace Bazaar.Secrets;

/// <summary>
/// The default <see cref="IAgentCredentialIssuer"/>. It never issues a credential, for any agent, so nothing is
/// re-issued until a real issuer is configured. It fails closed on purpose: a missing issuer must block re-issue, not allow it.
/// </summary>
public sealed class FailClosedAgentCredentialIssuer : IAgentCredentialIssuer
{
    private static readonly string _noIssuerDetail = "no credential issuer is configured";

    /// <summary>Always returns <see cref="CredentialIssueOutcome.NotAvailable"/>, whatever the agent's state.</summary>
    public Task<CredentialIssueResult> IssueAsync(AgentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CredentialIssueResult.NotAvailable(_noIssuerDetail));
    }
}
