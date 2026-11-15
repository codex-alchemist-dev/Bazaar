namespace Bazaar.Secrets;

/// <summary>A short-lived, per-agent, scoped token. The plaintext <see cref="Token"/> is returned once and never stored.</summary>
public sealed record AgentCredential(string IssuanceId, string Token, string Scope, DateTimeOffset ExpiresAt)
{
    /// <summary>The token is redacted here, so logging or interpolating a credential never leaks it.</summary>
    public override string ToString() =>
        $"AgentCredential {{ IssuanceId = {IssuanceId}, Scope = {Scope}, ExpiresAt = {ExpiresAt:O}, Token = [redacted] }}";
}

/// <summary>Raised when a credential is refused, for example because the agent is Quarantined or Revoked.</summary>
public sealed class AgentCredentialDeniedException(string message) : InvalidOperationException(message);

/// <summary>Issues and revokes per-agent credentials (doc 14). Only Active agents can receive one.</summary>
public interface IAgentCredentialIssuer
{
    /// <summary>Issues a credential for an Active agent. Refused for any other state. Lifetime must be between one minute and 24 hours.</summary>
    Task<AgentCredential> IssueAsync(AgentId agent, string scope, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Revokes one issuance. Idempotent: revoking again changes nothing and is not audited twice.</summary>
    Task RevokeAsync(AgentId agent, string issuanceId, CancellationToken ct);
}
