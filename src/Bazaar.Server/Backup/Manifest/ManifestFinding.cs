namespace Bazaar.Server.Backup.Manifest;

public enum ManifestFindingKind
{
    /// <summary>Listed in the manifest but absent from the files.</summary>
    Missing,

    /// <summary>Present in the files but not listed in the manifest.</summary>
    Unexpected,

    /// <summary>Listed and present, but the length or SHA-256 differs.</summary>
    HashMismatch,
}

/// <summary>One difference between a manifest and the files. <see cref="Expected"/> and <see cref="Actual"/> are null when not applicable.</summary>
public sealed record ManifestFinding(ManifestFindingKind Kind, string Path, string? Expected, string? Actual);

/// <summary>Result of checking files against a manifest. Findings are sorted by path (ordinal), then kind.</summary>
public sealed record ManifestVerificationResult(IReadOnlyList<ManifestFinding> Findings)
{
    public bool IsValid => Findings.Count == 0;
}
