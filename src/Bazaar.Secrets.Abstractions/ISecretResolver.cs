namespace Bazaar.Server.Secrets;

/// <summary>Resolves bootstrap secret references (doc 03). Only the key-encryption key and the database credential use this; provider and GitHub App keys go through <c>ISecretStore</c> (doc 14).</summary>
public interface ISecretResolver
{
    /// <summary>True when this resolver understands the reference's scheme.</summary>
    bool CanResolve(string reference);

    /// <summary>Returns the secret value. Failures throw <see cref="SecretResolutionException"/>, which names the reference and never the value.</summary>
    ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken);
}
