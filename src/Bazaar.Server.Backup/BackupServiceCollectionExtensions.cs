using Bazaar.Server.Backup.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Backup;

/// <summary>
/// Backup component registration. Only host composition calls <see cref="AddBazaarBackup"/>.
/// TODO(component): switch to the component model's registration once PR #125 merges; the hosted job schedule
/// (<c>backup.check_interval_hours</c>) is not wired yet.
/// </summary>
public static class BackupServiceCollectionExtensions
{
    public static IServiceCollection AddBazaarBackup(this IServiceCollection services)
    {
        services.AddSingleton<IBackupRunLog, InMemoryBackupRunLog>();
        return services;
    }
}
