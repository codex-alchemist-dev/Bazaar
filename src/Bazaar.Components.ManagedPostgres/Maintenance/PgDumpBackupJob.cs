using System.Globalization;
using System.Security.Cryptography;
using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>One verified logical backup: the dump, its SHA-256, and when it was taken.</summary>
public sealed record PgDumpBackup(string DumpPath, long Bytes, string Sha256, int Major, string Database, DateTimeOffset TakenAt);

/// <summary>
/// The scheduled logical backup (packaging.md section 3, doc 33). It runs <c>pg_dump -Fc</c> into a <c>.partial</c> file,
/// lists the archive with <c>pg_restore --list</c> to prove it is readable, then renames it into place and writes a
/// <c>.sha256</c> sidecar. A failed run leaves no file behind, so the newest dump on disk is always a verified one.
/// Dumps are logical copies for selective restore; point-in-time recovery stays with the base backup and WAL path.
/// Comment-only change: it makes CodeQL Analyze run on this head, since the bot-made merge commit did not trigger it.
/// </summary>
public sealed class PgDumpBackupJob(IPostgresCommandRunner runner, BundleLayout layout, TimeProvider time)
{
    public const string Extension = ".dump";
    public const string ChecksumExtension = ".sha256";
    private const string _partialExtension = ".partial";

    /// <summary>Takes one dump. The cluster must be running. <paramref name="keep"/> newest dumps for this major survive; older ones are deleted.</summary>
    public async Task<PgDumpBackup> RunAsync(PgConnection connection, string backupDirectory, int keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        if (keep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "Keep at least one dump.");
        }

        Directory.CreateDirectory(backupDirectory);
        var takenAt = time.GetUtcNow();
        var name = $"bazaar-pg{connection.Major}-{takenAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}{Extension}";
        var final = Path.Combine(backupDirectory, name);
        var partial = final + _partialExtension;

        try
        {
            var dump = PgDumpBackupPlanner.PlanDump(layout, connection.Major, partial, connection.Port, connection.User, connection.Database);
            var written = await runner.RunAsync(dump.FileName, dump.Arguments, cancellationToken, connection.Environment);
            if (written.ExitCode != 0)
            {
                throw new PgMaintenanceException($"pg_dump exited with code {written.ExitCode}: {Trim(written.Output)}");
            }

            var listing = PgDumpBackupPlanner.PlanRestoreCheck(layout, connection.Major, partial);
            var listed = await runner.RunAsync(listing.FileName, listing.Arguments, cancellationToken);
            if (listed.ExitCode != 0)
            {
                throw new PgMaintenanceException($"pg_restore could not read the new dump: {Trim(listed.Output)}");
            }

            var info = new FileInfo(partial);
            if (!info.Exists || info.Length == 0)
            {
                throw new PgMaintenanceException("pg_dump reported success but wrote no bytes.");
            }

            var sha = await HashAsync(partial, cancellationToken);
            File.Move(partial, final, overwrite: false);
            await File.WriteAllTextAsync(final + ChecksumExtension, $"{sha}  {name}\n", cancellationToken);

            Prune(backupDirectory, connection.Major, keep);
            return new PgDumpBackup(final, info.Length, sha, connection.Major, connection.Database, takenAt);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> dumps for one major. Names sort by time, so the newest come last.</summary>
    internal static void Prune(string backupDirectory, int major, int keep)
    {
        var prefix = $"bazaar-pg{major}-";
        var dumps = Directory.EnumerateFiles(backupDirectory, prefix + "*" + Extension)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var old in dumps.Take(Math.Max(0, dumps.Count - keep)))
        {
            TryDelete(Path.Combine(backupDirectory, old));
            TryDelete(Path.Combine(backupDirectory, old + ChecksumExtension));
        }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: the caller already has the error that matters.
        }
    }

    private static string Trim(string text) => text.Length <= 500 ? text.Trim() : text[..500].Trim();
}
