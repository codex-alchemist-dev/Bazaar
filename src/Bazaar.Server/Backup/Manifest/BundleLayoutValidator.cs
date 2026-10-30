namespace Bazaar.Server.Backup.Manifest;

public enum BundleLayoutFindingKind
{
    /// <summary>The bundle has no manifest file at <see cref="BundleLayoutValidator.ManifestPath"/>.</summary>
    ManifestMissing,

    /// <summary>The manifest file exists but does not parse, or lists itself.</summary>
    ManifestMalformed,
}

public sealed record BundleLayoutFinding(BundleLayoutFindingKind Kind, string Detail);

/// <summary>
/// Result of validating an export bundle. <see cref="Contents"/> is null when the manifest is missing or malformed,
/// because the payload cannot be checked without a valid manifest.
/// </summary>
public sealed record BundleLayoutResult(IReadOnlyList<BundleLayoutFinding> LayoutFindings, ManifestVerificationResult? Contents)
{
    public bool IsValid => LayoutFindings.Count == 0 && Contents is { IsValid: true };
}

/// <summary>Checks that a bundle has a well-formed manifest and that its payload matches it. Pure: no I/O.</summary>
public static class BundleLayoutValidator
{
    /// <summary>Manifest location at the bundle root (JSON, see <see cref="ManifestCodec"/>). Chosen here; doc 33 does not name it.</summary>
    public const string ManifestPath = "MANIFEST.json";

    public static BundleLayoutResult Validate(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var payload = new List<(string Path, byte[] Bytes)>();
        byte[]? manifestBytes = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            var normal = BundlePath.Normalize(path);
            if (!seen.Add(normal))
            {
                throw new ArgumentException($"Duplicate bundle path after normalisation: '{normal}'.", nameof(files));
            }

            if (normal == ManifestPath)
            {
                manifestBytes = bytes;
            }
            else
            {
                payload.Add((normal, bytes));
            }
        }

        if (manifestBytes is null)
        {
            return new BundleLayoutResult([new BundleLayoutFinding(BundleLayoutFindingKind.ManifestMissing, ManifestPath)], null);
        }

        var parsed = ManifestCodec.TryParse(manifestBytes);
        if (parsed.Manifest is null)
        {
            return new BundleLayoutResult([new BundleLayoutFinding(BundleLayoutFindingKind.ManifestMalformed, parsed.Error ?? "Unknown parse error.")], null);
        }

        if (parsed.Manifest.Entries.Any(e => BundlePath.Normalize(e.Path) == ManifestPath))
        {
            return new BundleLayoutResult([new BundleLayoutFinding(BundleLayoutFindingKind.ManifestMalformed, "Manifest lists itself.")], null);
        }

        return new BundleLayoutResult([], ManifestVerifier.Verify(parsed.Manifest, payload));
    }
}
