namespace Bazaar.Secrets;

/// <summary>
/// The default <see cref="IAgentCredentialIssuer"/>. It never issues a credential, for any agent, so nothing is re-issued until a
/// real issuer is configured. It fails closed on purpose: a missing issuer must block re-issue, not allow it.
/// </summary>
public sealed class FailClosedAgentCredentialIssuer : IAgentCredentialIssuer
{
    private const string _noIssuerMessage = "no credential issuer is configured";

    /// <summary>Always throws <see cref="InvalidOperationException"/>, whatever the agent's state.</summary>
    public Task<AgentCredential> IssueAsync(AgentId agent, SecretScope scope, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException(_noIssuerMessage);
    }

    /// <summary>Nothing is ever issued, so there is nothing to revoke. Always completes.</summary>
    public Task RevokeAsync(AgentId agent, string issuanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(issuanceId);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
