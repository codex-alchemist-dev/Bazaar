using Bazaar.Contracts.Backup;

namespace Bazaar.Server.Components.Backup.Jobs;

/// <summary>Records runs for the status card and alerts. TODO(component): back this with the component's table once the migration lands.</summary>
public interface IBackupRunLog
{
    void Record(BackupRun run);

    IReadOnlyList<BackupRun> Recent(int count);
}

/// <summary>Process-local run log. Good enough for tests and for the drill; lost on restart.</summary>
public sealed class InMemoryBackupRunLog : IBackupRunLog
{
    private readonly object _gate = new();
    private readonly List<BackupRun> _runs = [];

    public void Record(BackupRun run)
    {
        lock (_gate)
        {
            _runs.RemoveAll(r => r.RunId == run.RunId);
            _runs.Add(run);
        }
    }

    public IReadOnlyList<BackupRun> Recent(int count)
    {
        lock (_gate)
        {
            return _runs.OrderByDescending(r => r.StartedAt).Take(count).ToList();
        }
    }
}
