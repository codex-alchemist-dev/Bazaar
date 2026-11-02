namespace Bazaar.Server.Security.Quarantine;

/// <summary>Revokes every token and lease an agent holds (doc 15, "Credentials, mounts, browser"; kill switch).</summary>
public interface IAgentTokenRevoker
{
    /// <summary>Revokes the agent's tokens. Must be idempotent.</summary>
    Task RevokeAsync(string agentId, CancellationToken ct = default);
}
