using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Host.Safety;

/// <summary>
/// <see cref="IVmPauser"/> for a host with no VM backend configured. It never reports a pause as done. Containment then fails
/// visibly, and the stop stays in force, instead of quietly skipping the VMs. Replaced when the VM backend lands.
/// </summary>
public sealed class UnavailableVmPauser : IVmPauser
{
    /// <inheritdoc />
    public Task PauseAsync(string agentId, CancellationToken ct = default) =>
        throw new InvalidOperationException("No VM backend is configured, so the agent's VMs cannot be paused. The stop stays in force.");

    /// <inheritdoc />
    public Task ResumeAsync(string agentId, CancellationToken ct = default) =>
        throw new InvalidOperationException("No VM backend is configured, so the agent's VMs cannot be resumed.");
}

/// <summary>
/// <see cref="IAgentTokenRevoker"/> for a host with no credential revoker configured. Revoke throws, so containment never claims
/// tokens were revoked when they were not. Resume reports the revocation as not reversible. Replaced when the secrets registry lands.
/// </summary>
public sealed class UnavailableTokenRevoker : IAgentTokenRevoker
{
    /// <inheritdoc />
    public Task RevokeAsync(string agentId, CancellationToken ct = default) =>
        throw new InvalidOperationException("No token revoker is configured, so the agent's tokens cannot be revoked. The stop stays in force.");

    /// <inheritdoc />
    public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult(new TokenResumeResult(TokenResumeOutcome.NotReversible, "No token revoker is configured; tokens must be issued separately."));
}
