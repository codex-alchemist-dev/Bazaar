using Bazaar.Secrets;
using Bazaar.Server.Identity;

namespace Bazaar.Server.Security.Credentials;

/// <summary>
/// Reads the value behind a leased credential from <see cref="ISecretStore"/> (doc 14, "Leased credential values"
/// [PROPOSED]): the agent's own scope, the name <c>credential.&lt;credential id, 32 hex digits&gt;</c>, read by host
/// component <see cref="Component"/> for purpose <see cref="PurposeValue"/>.
/// </summary>
/// <remarks>
/// Returns null when the agent id is not a valid secret agent id or the store holds no such secret, so the observer logs
/// it and tracks nothing. A policy denial throws. Never logs or returns anything beyond the bytes it hands the caller.
/// </remarks>
public sealed class SecretStoreLeasedCredentialValueSource(ISecretStore store) : ILeasedCredentialValueSource
{
    /// <summary>Host component name the read is made under. Policy must grant it a read of the agent scope.</summary>
    public const string Component = "lease-matcher";

    /// <summary>Purpose the read names.</summary>
    public const string PurposeValue = "lease.match";

    /// <summary>The secret name for a credential id.</summary>
    public static SecretName NameFor(CredentialId credentialId) =>
        new("credential." + credentialId.Value.ToString("N"));

    /// <inheritdoc />
    public async ValueTask<byte[]?> ResolveAsync(string agentId, CredentialId credentialId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        AgentId agent;
        try
        {
            agent = new AgentId(agentId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            using var value = await store.GetAsync(
                new HostCaller(Component),
                SecretScope.ForAgent(agent),
                NameFor(credentialId),
                new SecretPurpose(PurposeValue),
                ct).ConfigureAwait(false);
            return value.Bytes.ToArray();
        }
        catch (SecretNotFoundException)
        {
            return null;
        }
    }
}
