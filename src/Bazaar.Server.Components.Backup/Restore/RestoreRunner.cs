using Bazaar.Contracts.Backup;

namespace Bazaar.Server.Components.Backup.Restore;

/// <summary>Thrown when the restore target already holds files. Restores never run in place (doc 33, restore rule 1).</summary>
public sealed class RestoreTargetNotEmptyException(string path) : IOException($"Restore target {path} is not empty.");

/// <summary>Thrown when the base set's PG_VERSION is not the major version the caller expects (doc 33, restore rule 5).</summary>
public sealed class RestoreSchemaMismatchException(int found, int expected)
    : InvalidOperationException($"Base set is PostgreSQL {found}; this server expects {expected}.");

/// <summary>
/// One restore. <see cref="RecoveryTargetTime"/> asks for point-in-time recovery; <see cref="BaseOnly"/> must be set
/// explicitly to restore only the base set (doc 33 prefers PITR). <see cref="RestoreCommand"/> is the <c>restore_command</c>
/// that fetches archived WAL, written as a single-quoted setting.
/// </summary>
public sealed record RestoreRequest(
    string BaseSetPath,
    string TargetDataDirectory,
    int ExpectedMajorVersion,
    DateTimeOffset? RecoveryTargetTime,
    string? RestoreCommand,
    bool BaseOnly = false);

/// <summary>What the restore wrote. The caller starts PostgreSQL on the target; this runner never does.</summary>
public sealed record RestoreResult(string TargetDataDirectory, int FilesCopied, long Bytes, bool PointInTime);

/// <summary>
/// Restores a base set into an empty directory and writes the recovery settings (doc 33 WP8). It refuses a non-empty
/// target, a schema mismatch, and a point-in-time request without a restore command. It never touches an existing data root.
/// TODO(component): the soul-folder consistency marker check (doc 33 restore rule 4) lands with Soul folder snapshots (WP6).
/// </summary>
public static class RestoreRunner
{
    public static async Task<RestoreResult> RunAsync(RestoreRequest request, CancellationToken ct)
    {
        if (!request.BaseOnly && (request.RecoveryTargetTime is null || string.IsNullOrWhiteSpace(request.RestoreCommand)))
            throw new ArgumentException("Point-in-time restore needs a recovery target time and a restore command. Set BaseOnly to restore the base set alone.", nameof(request));
        if (request.RecoveryTargetTime is { } t && t.Offset != TimeSpan.Zero)
            throw new ArgumentException("Recovery target time must be UTC.", nameof(request));

        var versionFile = Path.Combine(request.BaseSetPath, "PG_VERSION");
        if (!File.Exists(versionFile))
            throw new InvalidDataException("Base set has no PG_VERSION file.");
        var found = ParseMajor(await File.ReadAllTextAsync(versionFile, ct).ConfigureAwait(false));
        if (found != request.ExpectedMajorVersion)
            throw new RestoreSchemaMismatchException(found, request.ExpectedMajorVersion);

        var target = Path.GetFullPath(request.TargetDataDirectory);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new RestoreTargetNotEmptyException(target);
        Directory.CreateDirectory(target);

        var files = 0;
        long bytes = 0;
        foreach (var source in Directory.EnumerateFiles(request.BaseSetPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(request.BaseSetPath, source);
            if (relative == BaseBackupJobFiles.Sidecar)
                continue;
            var dest = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: false);
            files++;
            bytes += new FileInfo(dest).Length;
        }

        var pitr = !request.BaseOnly;
        if (pitr)
        {
            var conf = new System.Text.StringBuilder();
            conf.Append("restore_command = ").Append(Quote(request.RestoreCommand!)).Append('\n');
            conf.Append("recovery_target_time = ").Append(Quote(request.RecoveryTargetTime!.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff+00", System.Globalization.CultureInfo.InvariantCulture))).Append('\n');
            conf.Append("recovery_target_action = 'promote'\n");
            // The restored instance must never archive into the primary's archive; a second timeline would corrupt it.
            conf.Append("archive_mode = 'off'\n");
            await File.AppendAllTextAsync(Path.Combine(target, "postgresql.auto.conf"), conf.ToString(), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(target, "recovery.signal"), string.Empty, ct).ConfigureAwait(false);
        }

        return new RestoreResult(target, files, bytes, pitr);
    }

    /// <summary>PostgreSQL settings quote with single quotes, doubling any inside the value.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static int ParseMajor(string text)
    {
        var trimmed = text.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? trimmed : trimmed[..dot];
        return int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major
            : throw new InvalidDataException("PG_VERSION is not a version number.");
    }
}

internal static class BaseBackupJobFiles
{
    public const string Sidecar = "bazaar-backup-set.json";
}
