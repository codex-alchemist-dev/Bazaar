using Bazaar.Server.Identity;

namespace Bazaar.Server.Security.Credentials;

/// <summary>
/// Host-side source of the value behind a leased credential, for <see cref="LeasedCredentialObserver"/> (doc 15, "Reads for
/// modules"). The host implements it over <c>ISecretStore</c>; the lease itself holds ids only (doc 14).
/// </summary>
/// <remarks>
/// The mapping from a credential id to a secret name is not specified in the design docs, so this port leaves it to the host.
/// An implementation returns null when it cannot resolve a value. It must not log or retain the bytes.
/// </remarks>
public interface ILeasedCredentialValueSource
{
    /// <summary>Resolves the value of the credential leased to the agent, or null when there is none to resolve.</summary>
    /// <param name="agentId">The agent the lease was issued to.</param>
    /// <param name="credentialId">The credential the lease refers to.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The value as UTF-8 bytes. The caller zeroes the array after use.</returns>
    ValueTask<byte[]?> ResolveAsync(string agentId, CredentialId credentialId, CancellationToken ct);
}
