using Bazaar.Contracts.Backup;
using Bazaar.Server.Components.Backup.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components.Backup.Offsite;

/// <summary>Chooses the <see cref="IBackupTarget"/> for one upload from the current policy.</summary>
public interface IBackupTargetResolver
{
    /// <summary>Reads the policy now and returns the target. Throws when a configured target cannot be built.</summary>
    Task<IBackupTarget> ResolveAsync(CancellationToken ct);
}

/// <summary>
/// Builds the offsite target per upload (doc 33, "Offsite composition [PROPOSED]"). A <see cref="ResticBackupTarget"/> exists
/// only for the restic kinds, and it carries the cookie flag as read in this call. With <c>none</c> the result is a
/// <see cref="NoOpBackupTarget"/>.
/// </summary>
public sealed class BackupTargetResolver(
    IBackupOffsiteOptions options,
    IProcessRunner runner,
    IOffsiteCredentialSource credentials,
    IServiceProvider services,
    TimeProvider time) : IBackupTargetResolver
{
    /// <summary>Service key of the operator's own target for <see cref="OffsiteTargetKind.Custom"/>.</summary>
    public const string CustomTargetKey = "custom";

    public async Task<IBackupTarget> ResolveAsync(CancellationToken ct)
    {
        var current = await options.GetAsync(ct).ConfigureAwait(false);
        switch (current.Target)
        {
            case OffsiteTargetKind.None:
                return new NoOpBackupTarget(time);

            case OffsiteTargetKind.Custom:
                return services.GetKeyedService<IBackupTarget>(CustomTargetKey)
                    ?? throw new InvalidOperationException("backup.offsite_target is custom but no IBackupTarget is registered with the key 'custom'.");

            case OffsiteTargetKind.ResticS3:
            case OffsiteTargetKind.ResticB2:
                if (string.IsNullOrWhiteSpace(current.Repository))
                    throw new InvalidOperationException("backup.offsite_repository is empty.");
                if (string.IsNullOrWhiteSpace(current.ResticPath))
                    throw new InvalidOperationException("backup.offsite_restic_path is empty.");
                IReadOnlyDictionary<string, string> environment = string.IsNullOrWhiteSpace(current.CredentialsRef)
                    ? new Dictionary<string, string>()
                    : OffsiteCredentialParser.Parse(await credentials.ReadAsync(current.CredentialsRef, ct).ConfigureAwait(false));
                var name = current.Target == OffsiteTargetKind.ResticS3 ? "restic-s3" : "restic-b2";
                return new ResticBackupTarget(runner, current.ResticPath, current.Repository, environment, name, current.IncludeCookieProfiles);

            default:
                throw new InvalidOperationException("Unknown offsite target.");
        }
    }
}
