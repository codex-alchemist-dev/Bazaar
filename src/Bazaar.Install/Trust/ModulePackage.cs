using Bazaar.Contracts.Modules;

namespace Bazaar.Install.Trust;

/// <summary>
/// The signed part of a module package (doc 48 section 1). The signed fields are the id, the version, the publisher id,
/// the declared permissions, the content digest and the host requirements. The signer key id names the publisher key to
/// verify against, and that key must belong to the named publisher and cover the module id.
/// </summary>
/// <param name="ModuleId">The module id, as published.</param>
/// <param name="Version">The module version string, as published.</param>
/// <param name="PublisherId">The publisher id from the manifest. Signed, and checked against the owner of the signing key.</param>
/// <param name="DeclaredPermissions">The permissions the module declares, as strings. Compared and signed as a set.</param>
/// <param name="ContentDigestSha256Hex">The SHA-256 digest of the package content, in lowercase hex.</param>
/// <param name="SignerKeyId">The publisher key id that the signature claims to come from.</param>
/// <param name="Host">The host machines the module can run on (owner, 2026-11-16). Signed, so a publisher cannot widen it after signing.</param>
/// <param name="Signature">The signature bytes: ECDSA P-256 over the canonical form, IEEE P1363 (64 bytes).</param>
public sealed record ModulePackage(
    string ModuleId,
    string Version,
    string PublisherId,
    IReadOnlySet<string> DeclaredPermissions,
    string ContentDigestSha256Hex,
    string SignerKeyId,
    HostRequirements Host,
    byte[] Signature);
