using System.Security.Cryptography;

namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// A publisher public key in the server trust store (doc 48 section 1: publisher id, key id and validity dates).
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
    bool Revoked = false);
