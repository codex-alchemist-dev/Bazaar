namespace Bazaar.Kodo.Runtime;

/// <summary>The first stage that failed when a manifest was verified, or <see cref="Verified"/>.</summary>
public enum RuntimeManifestStatus
{
    /// <summary>The manifest is signed, well formed and every listed file matches, and nothing extra is in the slot.</summary>
    Verified,

    /// <summary>No manifest bytes were given.</summary>
    Missing,

    /// <summary>The bytes are not an envelope, or the manifest breaks the layout rules (paths, ids, hashes, order).</summary>
    Malformed,

    /// <summary>The manifest carries no signature.</summary>
    NoSignature,

    /// <summary>The signing key is not in the trust root.</summary>
    UnknownKey,

    /// <summary>The signing key is outside its validity window.</summary>
    KeyOutsideValidity,

    /// <summary>The signing key names an algorithm with no verifier.</summary>
    UnsupportedAlgorithm,

    /// <summary>The signature does not match the manifest, which means it was changed after signing.</summary>
    BadSignature,

    /// <summary>The manifest is authentic, but a file in the slot is missing, changed, or not listed.</summary>
    FileMismatch,
}

/// <summary>The kind of disagreement between a manifest and the slot's files.</summary>
public enum RuntimeFileProblemKind
{
    /// <summary>The manifest lists the file, and the slot does not have it.</summary>
    Missing,

    /// <summary>The file size differs from the manifest.</summary>
    SizeMismatch,

    /// <summary>The file size matches, but its SHA-256 differs.</summary>
    HashMismatch,

    /// <summary>The slot has a file that the manifest does not list.</summary>
    Unlisted,
}

/// <summary>One file that disagrees with the manifest. Hashes are lowercase hex, and null where not applicable.</summary>
public sealed record RuntimeFileProblem(string Path, RuntimeFileProblemKind Kind, string? ExpectedSha256, string? ActualSha256);

/// <summary>
/// The outcome of verifying a slot. Only <see cref="RuntimeManifestStatus.Verified"/> lets the slot run.
/// <see cref="FileProblems"/> lists every disagreement, even when the signature already failed, so evidence is complete.
/// </summary>
public sealed record RuntimeVerificationResult(RuntimeManifestStatus Status, IReadOnlyList<RuntimeFileProblem> FileProblems)
{
    /// <summary>True only when the status is <see cref="RuntimeManifestStatus.Verified"/>.</summary>
    public bool IsVerified => Status == RuntimeManifestStatus.Verified;
}
