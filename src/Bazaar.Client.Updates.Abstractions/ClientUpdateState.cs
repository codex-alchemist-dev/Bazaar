namespace Bazaar.Client.Updates;

/// <summary>Where the client updater is in its check, download, apply cycle.</summary>
public enum ClientUpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,

    /// <summary>The package is downloaded and verified. <see cref="IClientUpdater.ApplyAsync"/> may now restart into it.</summary>
    Ready,

    /// <summary>The offer was refused by the decision rules (not newer, invalid version, insecure URL).</summary>
    Refused,
    Failed,

    /// <summary>This process is not a Velopack install (a dev run or a portable copy), so it cannot update itself.</summary>
    Unsupported,
}

/// <summary>Why a cycle failed. Failures are explicit and never read as "no update".</summary>
public enum ClientUpdateFailure
{
    None,
    ManifestMissing,
    VersionMismatch,
    VerificationFailed,
    PackageTooLarge,
    Error,
}

/// <summary>A snapshot of the updater, safe to render in the UI.</summary>
/// <param name="Phase">The current phase.</param>
/// <param name="Channel">The channel the cycle ran on.</param>
/// <param name="InstalledVersion">The running version, or null when it is not an installed build.</param>
/// <param name="OfferedVersion">The newer version on offer, once a check found one.</param>
/// <param name="DownloadPercent">Download progress, 0 to 100.</param>
/// <param name="Failure">The failure kind when <paramref name="Phase"/> is <see cref="ClientUpdatePhase.Failed"/>.</param>
/// <param name="Detail">A short human-readable reason for a refusal or failure.</param>
public sealed record ClientUpdateState(
    ClientUpdatePhase Phase,
    UpdateChannel Channel,
    string? InstalledVersion = null,
    string? OfferedVersion = null,
    int DownloadPercent = 0,
    ClientUpdateFailure Failure = ClientUpdateFailure.None,
    string? Detail = null);
