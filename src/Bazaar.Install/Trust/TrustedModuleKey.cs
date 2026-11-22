using System.Security.Cryptography;

namespace Bazaar.Install.Trust;

/// <summary>
/// A publisher public key in the server trust store (doc 48 section 1: publisher id, key id and validity dates),
/// with the module ids its publisher may sign.
/// The store owns the <see cref="ECDsa"/> instance and disposes it.
/// </summary>
/// <param name="KeyId">The key id that packages name.</param>
/// <param name="PublisherId">The publisher that owns the key.</param>
/// <param name="PublicKey">The ECDSA P-256 public key.</param>
/// <param name="NotBefore">The first instant the key is valid, inclusive.</param>
/// <param name="NotAfter">The first instant the key is no longer valid, exclusive.</param>
/// <param name="Revoked">True when the key has been revoked. A revoked key never verifies.</param>
public sealed record TrustedModuleKey(
    string KeyId,
    string PublisherId,
    ECDsa PublicKey,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool Revoked = false)
{
    /// <summary>
    /// The module ids this key may sign. A scope covers the id equal to it and every id below it (<c>acme</c> covers
    /// <c>acme</c> and <c>acme.tools</c>, never <c>acmex</c>). Empty means the key signs nothing: a trusted key without a
    /// scope would let any registered publisher ship an update to any module, first-party ones included.
    /// </summary>
    public IReadOnlyList<string> ModuleIdScopes { get; init; } = [];

    /// <summary>Whether <paramref name="moduleId"/> is inside one of the key's scopes, by whole dot-separated labels.</summary>
    public bool Covers(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        foreach (var scope in ModuleIdScopes)
        {
            // A wildcard is not a scope, and a scope with an empty label would match ids nobody meant.
            if (!ModuleText.IsToken(scope) || scope.Contains('*', StringComparison.Ordinal) || scope.Split('.').Any(label => label.Length == 0))
            {
                continue;
            }

            if (moduleId.Equals(scope, StringComparison.Ordinal)
                || (moduleId.Length > scope.Length + 1
                    && moduleId[scope.Length] == '.'
                    && moduleId.StartsWith(scope, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
