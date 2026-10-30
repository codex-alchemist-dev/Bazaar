namespace Bazaar.Server.Backup.Manifest;

/// <summary>Builds a <see cref="BackupManifest"/> from (relative path, bytes) pairs. Pure: no I/O.</summary>
public static class ManifestWriter
{
    public static BackupManifest Build(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<BackupManifestEntry>();
        foreach (var (path, bytes) in files)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var normal = BundlePath.Normalize(path);
            if (!seen.Add(normal))
            {
                throw new ArgumentException($"Duplicate bundle path after normalisation: '{normal}'.", nameof(files));
            }

            entries.Add(new BackupManifestEntry(normal, bytes.LongLength, BundleHash.Sha256Hex(bytes)));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new BackupManifest(entries);
    }
}
