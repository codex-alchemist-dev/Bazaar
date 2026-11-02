namespace Bazaar.Server.Backup.Manifest;

/// <summary>Checks (relative path, bytes) pairs against a manifest. Pure and deterministic: no I/O.</summary>
public static class ManifestVerifier
{
    public static ManifestVerificationResult Verify(BackupManifest manifest, IEnumerable<(string Path, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);

        var expected = new Dictionary<string, BackupManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var normal = BundlePath.Normalize(entry.Path);
            if (!expected.TryAdd(normal, entry))
            {
                throw new ArgumentException($"Manifest lists '{normal}' more than once.", nameof(manifest));
            }
        }

        var actual = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var normal = BundlePath.Normalize(path);
            if (!actual.TryAdd(normal, bytes))
            {
                throw new ArgumentException($"Duplicate bundle path after normalisation: '{normal}'.", nameof(files));
            }
        }

        var findings = new List<ManifestFinding>();
        foreach (var (path, entry) in expected)
        {
            if (!actual.TryGetValue(path, out var bytes))
            {
                findings.Add(new ManifestFinding(ManifestFindingKind.Missing, path, entry.Sha256, null));
                continue;
            }

            var actualHash = BundleHash.Sha256Hex(bytes);
            if (bytes.LongLength != entry.Length || !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new ManifestFinding(
                    ManifestFindingKind.HashMismatch,
                    path,
                    BundleHash.Describe(entry.Length, entry.Sha256),
                    BundleHash.Describe(bytes.LongLength, actualHash)));
            }
        }

        foreach (var (path, bytes) in actual)
        {
            if (!expected.ContainsKey(path))
            {
                findings.Add(new ManifestFinding(ManifestFindingKind.Unexpected, path, null, BundleHash.Describe(bytes.LongLength, BundleHash.Sha256Hex(bytes))));
            }
        }

        findings.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Kind.CompareTo(b.Kind);
        });
        return new ManifestVerificationResult(findings);
    }
}
