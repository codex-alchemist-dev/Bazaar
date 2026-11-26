using Bazaar.Secrets;

namespace Bazaar.Server.Security.Credentials;

/// <summary>
/// Adds the default read grant for the leased-credential matcher to a secret policy (doc 14, "Leased credential values"
/// [PROPOSED]): host component <see cref="SecretStoreLeasedCredentialValueSource.Component"/> may read a secret named
/// <c>credential.&lt;32 hex digits&gt;</c> in an agent's own scope for purpose <see cref="SecretStoreLeasedCredentialValueSource.PurposeValue"/>.
/// Every other request goes to the inner policy unchanged, so the grant covers nothing else.
/// </summary>
public sealed class LeaseMatcherSecretPolicy(ISecretPolicy inner) : ISecretPolicy
{
    private const string _agentScopePrefix = "agent:";
    private const string _namePrefix = "credential.";
    private const int _hexLength = 32;

    /// <inheritdoc />
    public Task<bool> AllowsAsync(string component, SecretScope scope, SecretName? name, SecretPurpose purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return IsLeaseMatcherRead(component, scope, name, purpose)
            ? Task.FromResult(true)
            : inner.AllowsAsync(component, scope, name, purpose, ct);
    }

    private static bool IsLeaseMatcherRead(string component, SecretScope scope, SecretName? name, SecretPurpose purpose)
    {
        if (component != SecretStoreLeasedCredentialValueSource.Component
            || purpose.Value != SecretStoreLeasedCredentialValueSource.PurposeValue
            || !scope.Key.StartsWith(_agentScopePrefix, StringComparison.Ordinal)
            || name is null
            || !name.Value.StartsWith(_namePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = name.Value.AsSpan(_namePrefix.Length);
        if (hex.Length != _hexLength)
        {
            return false;
        }

        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }
}
