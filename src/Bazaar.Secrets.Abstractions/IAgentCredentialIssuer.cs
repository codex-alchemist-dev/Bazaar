namespace Bazaar.Secrets;

/// <summary>
/// Re-issues an agent's credential (for example after a release from quarantine). The real issuer is built
/// behind this contract by the secrets work; until one is configured, <see cref="FailClosedAgentCredentialIssuer"/> answers.
/// </summary>
/// <remarks>
/// Callers must only call <see cref="IssueAsync"/> for an agent that is <see cref="AgentState.Active"/>, or that was just released.
/// An issuer must never return a credential for a <see cref="AgentState.Revoked"/> agent. A revoked agent gets a new
/// <see cref="AgentId"/> through <see cref="IAgentRegistry.RegisterAsync"/> instead.
/// </remarks>
public interface IAgentCredentialIssuer
{
    /// <summary>Asks the issuer for a fresh credential for the agent. The result carries an outcome and a short detail only, never a secret.</summary>
    Task<CredentialIssueResult> IssueAsync(AgentId id, CancellationToken ct);
}

/// <summary>What the issuer did with a request for a credential.</summary>
public enum CredentialIssueOutcome
{
    /// <summary>A credential was issued and stored by the issuer. The value itself is never part of the result.</summary>
    Issued,

    /// <summary>No credential was issued. The detail says why.</summary>
    NotAvailable,
}

/// <summary>
/// The outcome of a credential re-issue request. It carries no token or key: only an <see cref="Outcome"/> and a short <see cref="Detail"/>.
/// </summary>
/// <param name="Outcome">Whether a credential was issued.</param>
/// <param name="Detail">A short, secret-free explanation for logs and the owner's view.</param>
public sealed record CredentialIssueResult(CredentialIssueOutcome Outcome, string Detail)
{
    /// <summary>A credential was issued. The detail must not contain the credential.</summary>
    public static CredentialIssueResult Issued(string detail) => new(CredentialIssueOutcome.Issued, detail);

    /// <summary>No credential could be issued, for the stated reason.</summary>
    public static CredentialIssueResult NotAvailable(string detail) => new(CredentialIssueOutcome.NotAvailable, detail);
}
