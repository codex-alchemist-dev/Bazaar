namespace Bazaar.Contracts.Soul;

/// <summary>
/// The manifest inside a Soul bundle export (docs/design/07-soul-system.md, work packages 8 and 9). The exporter writes
/// <c>manifest.json</c> and a detached <see cref="BundleSignature"/> over its exact bytes. Import verifies both before
/// anything leaves quarantine.
/// </summary>
/// <param name="FormatVersion">Bundle format version. 1 for the first format.</param>
/// <param name="SoulId">The Soul being exported.</param>
/// <param name="SoulVersion">The Soul version whose charter and preferences are in the bundle.</param>
/// <param name="ContentSha256">Lowercase hex content hash of that version (see <see cref="SoulVersion.ContentSha256"/>).</param>
/// <param name="Exporter">Who produced the bundle.</param>
/// <param name="ExportedAt">When the bundle was produced.</param>
/// <param name="Files">Every file in the bundle except the manifest and signature, with digests.</param>
/// <param name="Exclusions">Categories the exporter left out (for example secrets, personal fields). Recorded for the importer.</param>
public sealed record BundleManifest(
    int FormatVersion,
    Guid SoulId,
    int SoulVersion,
    string ContentSha256,
    string Exporter,
    DateTimeOffset ExportedAt,
    IReadOnlyList<BundleFile> Files,
    IReadOnlyList<string> Exclusions);

/// <summary>One file in a bundle.</summary>
/// <param name="Path">Relative path inside the bundle. Forward slashes; never absolute; never contains "..".</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the file bytes.</param>
/// <param name="Size">Byte length.</param>
public sealed record BundleFile(string Path, string Sha256, long Size);

/// <summary>A detached signature over the exact bytes of <c>manifest.json</c>.</summary>
/// <param name="KeyId">Identifier of the signing key.</param>
/// <param name="Algorithm">Signature algorithm name, for example "Ed25519".</param>
/// <param name="SignatureBase64">The signature bytes, base64.</param>
public sealed record BundleSignature(string KeyId, string Algorithm, string SignatureBase64);
