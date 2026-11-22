using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio.Abstractions;

/// <summary>
/// Asks the server whether a grant covers a tool and risk class for the requesting agent. The server owns grants
/// (doc 20); the real implementation arrives with the server-side grant store (doc 20 WP4).
/// </summary>
public interface IStudioGrantVerifier
{
    /// <summary>True only when the grant is valid and covers <paramref name="tool"/> at <paramref name="riskClass"/>. Any failure to ask must throw; callers fail closed.</summary>
    Task<bool> CoversAsync(string grantId, string tool, RiskClass riskClass, CancellationToken cancellationToken);
}
