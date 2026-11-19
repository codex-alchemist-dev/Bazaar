namespace Bazaar.Client.Updater.Linux;

/// <summary>The outcome of a check, an apply or a rollback. Every refusal leaves the installed build in place.</summary>
public enum UpdateStatus
{
    /// <summary>The server offered no usable manifest.</summary>
    NoManifest,

    /// <summary>The offered version equals the installed one.</summary>
    UpToDate,

    /// <summary>A newer release is on offer and passes every check that needs no download (check only).</summary>
    Available,

    /// <summary>The release was verified, extracted and <c>current</c> now points at it.</summary>
    Installed,

    /// <summary><c>current</c> was switched back to the previous release.</summary>
    RolledBack,

    /// <summary>There is no previous release to go back to.</summary>
    NothingToRollBack,

    /// <summary>The configured channel is unknown, or is Latest-Commit on an install that is not the owner's.</summary>
    ChannelRefused,

    /// <summary>The manifest's channel is not the configured one.</summary>
    ChannelMismatch,

    /// <summary>The offered version is older than the installed one.</summary>
    DowngradeRefused,

    /// <summary>The release has no <c>tar.gz</c> for this architecture.</summary>
    NoArtifact,

    /// <summary>A file is larger than its bound (declared size, operator ceiling, or the limits on the checksum and signature files).</summary>
    SizeExceeded,

    /// <summary>A file could not be fetched.</summary>
    DownloadFailed,

    /// <summary>The SHA-256 of the tarball, the inline value and the <c>.sha256</c> file do not all agree.</summary>
    ChecksumMismatch,

    /// <summary>The manifest names a different signing key than the one pinned in this build.</summary>
    FingerprintMismatch,

    /// <summary>The detached signature does not verify against the pinned key.</summary>
    SignatureInvalid,

    /// <summary><c>gpg</c> could not be run or the pinned key could not be loaded.</summary>
    SignatureToolFailed,

    /// <summary>The tarball is malformed or tries to write outside its release directory.</summary>
    ExtractionRejected,

    /// <summary>The file system refused a step of the install or the rollback.</summary>
    InstallFailed,
}

/// <summary>Result of an updater call.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Version">The release the call concerns (offered, installed or rolled back to), when there is one.</param>
/// <param name="Detail">A short reason, safe to log. Never holds key material.</param>
/// <param name="At">When the call finished.</param>
public sealed record UpdateResult(UpdateStatus Status, string? Version, string Detail, DateTimeOffset At)
{
    /// <summary>True for the outcomes that did what was asked: Installed, RolledBack, Available or UpToDate.</summary>
    public bool IsSuccess => Status is UpdateStatus.Installed or UpdateStatus.RolledBack or UpdateStatus.Available or UpdateStatus.UpToDate;
}
