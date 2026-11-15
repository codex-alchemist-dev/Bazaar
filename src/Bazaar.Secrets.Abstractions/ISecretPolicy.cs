namespace Bazaar.Secrets;

/// <summary>
/// One host component's permission to use one purpose on one secret, in one scope. A null <see cref="Name"/>
/// covers every secret in the scope; use it only for components the operator trusts with the whole scope.
/// </summary>
public sealed record SecretGrant(string Component, string ScopeKey, SecretName? Name, SecretPurpose Purpose);

/// <summary>
/// Decides whether a host component may use a secret for a purpose. Deny by default: without a matching grant
/// the answer is no. The store checks this before any value moves, and records the decision in the audit log.
/// </summary>
public interface ISecretPolicy
{
    Task<bool> AllowsAsync(string component, SecretScope scope, SecretName? name, SecretPurpose purpose, CancellationToken ct);
}

public static class SecretPurposes
{
    /// <summary>
    /// Required for writes, deletes and crypto-erase. Grant it only to the components that manage secrets.
    /// </summary>
    public static SecretPurpose Manage { get; } = new("secrets.manage");
}
