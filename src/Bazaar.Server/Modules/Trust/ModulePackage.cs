namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// The signed part of a module package (doc 48 section 1). The signed fields are the id, the version,
/// the declared permissions and the content digest. The signer key id names the publisher key to verify against.
/// </summary>
/// <param name="ModuleId">The module id, as published.</param>
/// <param name="Version">The module version string, as published.</param>
/// <param name="DeclaredPermissions">The permissions the module declares, as strings. Compared and signed as a set.</param>
/// <param name="ContentDigestSha256Hex">The SHA-256 digest of the package content, in lowercase hex.</param>
/// <param name="SignerKeyId">The publisher key id that the signature claims to come from.</param>
/// <param name="Signature">The signature bytes: ECDSA P-256 over the canonical form, IEEE P1363 (64 bytes).</param>
public sealed record ModulePackage(
    string ModuleId,
    string Version,
    IReadOnlySet<string> DeclaredPermissions,
    string ContentDigestSha256Hex,
    string SignerKeyId,
    byte[] Signature);
