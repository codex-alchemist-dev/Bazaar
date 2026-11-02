namespace Bazaar.Server.Backup.Manifest;

/// <summary>
/// A manifest of every file in a bundle, sorted by path (ordinal). The hash algorithm is fixed to SHA-256 by doc 33
/// (instance bundle: "SHA-256 manifest"); the file layout is this design's choice, see <see cref="ManifestCodec"/>.
/// </summary>
public sealed record BackupManifest(IReadOnlyList<BackupManifestEntry> Entries)
{
    /// <summary>Version of the text layout written by <see cref="ManifestCodec"/>.</summary>
    public const int FormatVersion = 1;

    /// <summary>Digest name written in the manifest header.</summary>
    public const string HashAlgorithm = "SHA-256";
}
