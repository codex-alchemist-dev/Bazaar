namespace Bazaar.Secrets;

/// <summary>
/// Issues and revokes an agent's credentials (for example when an agent is released from quarantine). The real issuer is built
/// behind this contract by the secrets work; until one is configured, <see cref="FailClosedAgentCredentialIssuer"/> refuses.
/// </summary>
/// <remarks>
/// <see cref="IssueAsync"/> must only issue for an agent that is <see cref="AgentState.Active"/>, or that was just released.
/// An issuer must never return a credential for a <see cref="AgentState.Revoked"/> agent. A revoked agent gets a new
/// <see cref="AgentId"/> through <see cref="IAgentRegistry.RegisterAsync"/> instead. A returned <see cref="AgentCredential"/>
/// carries its token, so the caller disposes it as soon as the token has been used.
/// </remarks>
public interface IAgentCredentialIssuer
{
    /// <summary>Issues a fresh credential for the agent, valid for <paramref name="lifetime"/> within <paramref name="scope"/>.</summary>
    /// <exception cref="InvalidOperationException">No issuer is configured, or the agent may not be issued a credential.</exception>
    Task<AgentCredential> IssueAsync(AgentId agent, SecretScope scope, TimeSpan lifetime, CancellationToken ct);

    /// <summary>
    /// Revokes one credential issued to the agent. Idempotent: revoking an issuance that is already revoked, or that was never
    /// issued, completes without error.
    /// </summary>
    Task RevokeAsync(AgentId agent, string issuanceId, CancellationToken ct);
}

/// <summary>
/// A credential issued to an agent. <see cref="Token"/> is secret: its <see cref="SecretValue.ToString"/> is redacted, so the
/// record's <see cref="object.ToString"/> never shows it, and the caller disposes the token once it has been used.
/// </summary>
/// <param name="IssuanceId">Identifies this issuance, for <see cref="IAgentCredentialIssuer.RevokeAsync"/>.</param>
/// <param name="Token">The secret credential value.</param>
/// <param name="Scope">The scope the credential was issued for.</param>
/// <param name="ExpiresAt">When the credential stops being valid.</param>
public sealed record AgentCredential(string IssuanceId, SecretValue Token, SecretScope Scope, DateTimeOffset ExpiresAt);
