using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Import;

/// <summary>Why a bundle was rejected. Each code names one check that failed.</summary>
public enum BundleRejectionCode
{
    /// <summary>The manifest is larger than <see cref="BundleImportOptions.MaxManifestBytes"/>.</summary>
    ManifestTooLarge,

    /// <summary>The manifest is not valid strict JSON, has unknown members, or is missing or has invalid required fields.</summary>
    ManifestMalformed,

    /// <summary>The manifest's <c>formatVersion</c> is not one this importer supports.</summary>
    UnknownFormatVersion,

    /// <summary>No signature was supplied, or it has no key id or no signature bytes.</summary>
    SignatureMissing,

    /// <summary>The signing key is not trusted.</summary>
    UnknownSigningKey,

    /// <summary>The signature does not verify over the manifest bytes.</summary>
    SignatureInvalid,

    /// <summary>A path is empty, has empty or "." segments, control characters, or other colons.</summary>
    PathInvalid,

    /// <summary>A path is absolute (starts with a slash).</summary>
    PathAbsolute,

    /// <summary>A path contains "..".</summary>
    PathTraversal,

    /// <summary>A path contains a backslash.</summary>
    PathBackslash,

    /// <summary>A path starts with a drive letter.</summary>
    PathDriveLetter,

    /// <summary>A path appears more than once, compared case-insensitively (the Windows host is case-insensitive).</summary>
    PathDuplicate,

    /// <summary>A bundle file is not listed in the manifest.</summary>
    FileNotInManifest,

    /// <summary>A manifest entry has no matching bundle file.</summary>
    ManifestEntryMissingFile,

    /// <summary>A manifest digest is not 64 lowercase hex characters.</summary>
    DigestMalformed,

    /// <summary>A file's SHA-256 does not match its manifest digest.</summary>
    DigestMismatch,

    /// <summary>A file's byte length does not match its manifest size (or the manifest size is negative).</summary>
    SizeMismatch,

    /// <summary>A file is larger than <see cref="BundleImportOptions.MaxFileBytes"/>.</summary>
    FileTooLarge,

    /// <summary>The bundle files together are larger than <see cref="BundleImportOptions.MaxTotalBytes"/>.</summary>
    TotalTooLarge,
}

/// <summary>One rejection reason, with the path it concerns when there is one.</summary>
/// <param name="Code">The check that failed.</param>
/// <param name="Path">The bundle path involved, or null for bundle-wide checks.</param>
public sealed record BundleRejection(BundleRejectionCode Code, string? Path = null);

/// <summary>
/// Result of importing a bundle. Either the bundle is quarantined (every check passed; the manifest is verified) or it is
/// rejected with the reasons. Nothing is written to the Soul version chain by either outcome.
/// </summary>
public abstract record BundleImportResult;

/// <summary>Every check passed. The bundle waits in quarantine for the owner's charter-diff review; it is not active.</summary>
/// <param name="Manifest">The verified manifest.</param>
public sealed record BundleQuarantined(BundleManifest Manifest) : BundleImportResult;

/// <summary>At least one check failed. The reasons list every failure found by the checks that ran.</summary>
/// <param name="Rejections">The failed checks, in the order they ran.</param>
public sealed record BundleRejected(IReadOnlyList<BundleRejection> Rejections) : BundleImportResult;
