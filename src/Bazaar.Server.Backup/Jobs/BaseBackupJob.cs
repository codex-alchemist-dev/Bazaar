using System.Text.Json;
using Bazaar.Contracts.Backup;
using Bazaar.Server.Backup.Engines;
using Bazaar.Server.Backup.Processes;
using Bazaar.Server.Backup.Retention;

namespace Bazaar.Server.Backup.Jobs;

/// <summary>
/// Takes one base backup, then applies retention (doc 33 WP3). Old sets are deleted only after the new one verified,
/// so a failed run never shrinks the set of recoverable copies. Each set carries a <c>bazaar-backup-set.json</c>
/// sidecar with its time, size and start WAL, which is how later runs rediscover sets after a restart.
/// </summary>
public sealed class BaseBackupJob(
    IBackupEngine engine,
    string setsRoot,
    int baseSetsKept,
    TimeProvider time,
    IBackupRunLog runLog,
    IProcessRunner? processes = null,
    string? archiveDirectory = null,
    string? pgBinDirectory = null)
{
    public const string SidecarName = "bazaar-backup-set.json";

    /// <summary>Runs one backup and retention pass. Returns the finished run record; failures are recorded, then returned as Failed.</summary>
    public async Task<BackupRun> RunAsync(CancellationToken ct)
    {
        var runId = Guid.CreateVersion7();
        var started = time.GetUtcNow();
        Record(runId, BackupKind.BaseBackup, BackupState.Running, started, null, 0, null, null);

        try
        {
            Directory.CreateDirectory(setsRoot);
            var destination = Path.Combine(setsRoot, "set-" + runId.ToString("N"));
            var set = await engine.TakeBaseBackupAsync(destination, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(set.Path, SidecarName), JsonSerializer.Serialize(new SidecarV1(set.SetId, set.TakenAt, set.Bytes, set.StartWal)), ct).ConfigureAwait(false);

            var existing = DiscoverSets(setsRoot).ToList();
            var plan = RetentionPlanner.Plan(existing, baseSetsKept);
            foreach (var old in plan.Delete)
                Directory.Delete(old.Path, recursive: true);
            await CleanWalAsync(plan.WalKeepFrom, ct).ConfigureAwait(false);

            var done = Record(runId, BackupKind.BaseBackup, BackupState.Succeeded, started, time.GetUtcNow(), set.Bytes, set.SetId,
                $"engine={engine.Name}; kept={plan.Keep.Count}; deleted={plan.Delete.Count}");
            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type and message only: process errors carry no arguments, and no secret is written here.
            return Record(runId, BackupKind.BaseBackup, BackupState.Failed, started, time.GetUtcNow(), 0, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Base sets on disk, read from their sidecars. Directories without a valid sidecar are ignored, never deleted.</summary>
    public static IEnumerable<BackupSet> DiscoverSets(string setsRoot)
    {
        if (!Directory.Exists(setsRoot))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(setsRoot, "set-*"))
        {
            var sidecar = Path.Combine(dir, SidecarName);
            if (!File.Exists(sidecar))
                continue;
            var v = JsonSerializer.Deserialize<SidecarV1>(File.ReadAllText(sidecar));
            if (v is null)
                continue;
            yield return new BackupSet(v.SetId, dir, v.TakenAt, v.Bytes, v.StartWal);
        }
    }

    private async Task CleanWalAsync(string walKeepFrom, CancellationToken ct)
    {
        if (processes is null || archiveDirectory is null || pgBinDirectory is null)
            return;
        var tool = Path.Combine(pgBinDirectory, OperatingSystem.IsWindows() ? "pg_archivecleanup.exe" : "pg_archivecleanup");
        var result = await processes.RunAsync(tool, [archiveDirectory, walKeepFrom], null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ProcessFailedException("pg_archivecleanup", result.ExitCode, result.StandardError);
    }

    private BackupRun Record(Guid runId, BackupKind kind, BackupState state, DateTimeOffset started, DateTimeOffset? finished, long bytes, string? setId, string? detail)
    {
        var run = new BackupRun(runId, kind, state, started, finished, bytes, setId, detail);
        runLog.Record(run);
        return run;
    }

    private sealed record SidecarV1(string SetId, DateTimeOffset TakenAt, long Bytes, string StartWal);
}
