namespace Bazaar.Server.Security.Quarantine;

/// <summary>Revokes every token and lease an agent holds (doc 15, "Credentials, mounts, browser"; kill switch).</summary>
public interface IAgentTokenRevoker
{
    /// <summary>Revokes the agent's tokens. Must be idempotent.</summary>
    Task RevokeAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// Asks to undo <see cref="RevokeAsync"/> for the agent. Revocation is permanent today and no token-issuing flow exists,
    /// so an implementation returns <see cref="TokenResumeResult.NotReversible"/> and issues nothing. An implementation
    /// that can restore tokens returns <see cref="TokenResumeResult.Restored"/>. Callers authorize before calling.
    /// </summary>
    Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IAgentTokenRevoker.ResumeAsync"/>.</summary>
public enum TokenResumeOutcome
{
    /// <summary>The revocation cannot be undone; the agent must be issued new tokens through a separate flow.</summary>
    NotReversible,

    /// <summary>The agent's tokens were restored.</summary>
    Restored,
}

/// <summary>Result of a token resume request, with a human-readable detail for the audit record.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Short explanation, written to the audit log.</param>
public sealed record TokenResumeResult(TokenResumeOutcome Outcome, string Detail)
{
    /// <summary>Result for a revoker whose revocation is permanent.</summary>
    public static TokenResumeResult NotReversible { get; } =
        new(TokenResumeOutcome.NotReversible, "Token revocation is not reversible; new tokens must be issued separately.");

    /// <summary>Result for a revoker that restored the agent's tokens.</summary>
    public static TokenResumeResult Restored { get; } = new(TokenResumeOutcome.Restored, "Tokens restored.");
}
