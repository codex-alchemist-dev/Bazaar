namespace Bazaar.Server.Components.Backup.Offsite;

/// <summary>Where backups go offsite (doc 33, <c>backup.offsite_target</c>). Wire form in the settings registry is snake_case.</summary>
public enum OffsiteTargetKind
{
    /// <summary>Nothing leaves the install. The shipped default.</summary>
    None,

    /// <summary>restic to an S3-compatible bucket.</summary>
    ResticS3,

    /// <summary>restic to Backblaze B2.</summary>
    ResticB2,

    /// <summary>An operator-supplied <c>IBackupTarget</c>, registered as the keyed service <see cref="BackupTargetResolver.CustomTargetKey"/>.</summary>
    Custom,
}

/// <summary>One immutable reading of the offsite policy (doc 33, "Offsite composition [PROPOSED]"). Holds no secret value.</summary>
/// <param name="Target">The configured target kind.</param>
/// <param name="ResticPath">The restic executable.</param>
/// <param name="Repository">The restic repository, for example <c>s3:https://host/bucket</c>.</param>
/// <param name="CredentialsRef">A secret reference, never a value; resolved by <see cref="IOffsiteCredentialSource"/>.</param>
/// <param name="IncludeCookieProfiles"><c>security.backup.include_cookie_profiles</c> at the moment of the reading.</param>
public sealed record BackupOffsiteSettings(
    OffsiteTargetKind Target = OffsiteTargetKind.None,
    string ResticPath = "restic",
    string Repository = "",
    string CredentialsRef = "",
    bool IncludeCookieProfiles = false);

/// <summary>
/// The offsite policy, read at upload time and never cached, so an operator change applies to the next upload without a
/// restart. The host implements it over the settings store.
/// </summary>
public interface IBackupOffsiteOptions
{
    Task<BackupOffsiteSettings> GetAsync(CancellationToken ct);
}

/// <summary>The default policy when the host supplies none: no offsite target.</summary>
public sealed class NoOffsiteOptions : IBackupOffsiteOptions
{
    public Task<BackupOffsiteSettings> GetAsync(CancellationToken ct) => Task.FromResult(new BackupOffsiteSettings());
}
