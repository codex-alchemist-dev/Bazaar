namespace Bazaar.Secrets;

/// <summary>
/// A fixed grant list loaded at bootstrap from operator configuration. It is deny by default: anything not listed is refused.
/// Changes take effect on restart, so a change never widens access mid-run.
/// </summary>
public sealed class StaticSecretPolicy : ISecretPolicy
{
    private readonly HashSet<SecretGrant> _grants;

    public StaticSecretPolicy(IEnumerable<SecretGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        _grants = new HashSet<SecretGrant>(grants);
    }

    public static StaticSecretPolicy DenyAll { get; } = new(Array.Empty<SecretGrant>());

    public Task<bool> AllowsAsync(string component, SecretScope scope, SecretName? name, SecretPurpose purpose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = _grants.Any(grant =>
            grant.Component == component
            && grant.ScopeKey == scope.Key
            && grant.Purpose == purpose
            && (grant.Name is null || grant.Name == name));
        return Task.FromResult(allowed);
    }
}
