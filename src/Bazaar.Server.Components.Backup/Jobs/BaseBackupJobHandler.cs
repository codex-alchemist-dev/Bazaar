using Bazaar.Server.Jobs;

namespace Bazaar.Server.Components.Backup.Jobs;

/// <summary>Runs the configured PostgreSQL base-backup and retention pass.</summary>
public sealed class BaseBackupJobHandler(BaseBackupJob backup) : IJobHandler
{
    public const string JobKind = "backup.base_backup";

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct) =>
        _ = await backup.RunAsync(ct).ConfigureAwait(false);
}
