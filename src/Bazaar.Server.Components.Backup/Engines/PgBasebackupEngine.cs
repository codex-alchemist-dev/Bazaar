using Bazaar.Contracts.Backup;
using Bazaar.Server.Components.Backup.Processes;

namespace Bazaar.Server.Components.Backup.Engines;

/// <summary>
/// Base backups through <c>pg_basebackup</c> (doc 33, Windows path and the default engine on every host). Streams the
/// WAL needed for consistency, then checks the result with <c>pg_verifybackup</c>. The connection string must not carry
/// a password; the operator supplies it through PGPASSFILE or a service-account setting.
/// </summary>
public sealed class PgBasebackupEngine(IProcessRunner runner, string binDirectory, string connectionString, TimeProvider time) : IBackupEngine
{
    public string Name => "pg_basebackup";

    public async Task<BackupSet> TakeBaseBackupAsync(string destinationDirectory, CancellationToken ct)
    {
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
            throw new IOException("Base backup destination must not exist yet.");

        var takenAt = time.GetUtcNow();
        var label = "bazaar-" + takenAt.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        var basebackup = Path.Combine(binDirectory, ExeName("pg_basebackup"));
        var result = await runner.RunAsync(
            basebackup,
            [
                "-d", connectionString,
                "-D", destinationDirectory,
                "-F", "p",
                "-X", "stream",
                "-c", "fast",
                "-l", label,
                "--manifest-checksums=SHA256",
            ],
            null,
            ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ProcessFailedException("pg_basebackup", result.ExitCode, result.StandardError);

        var verify = await runner.RunAsync(Path.Combine(binDirectory, ExeName("pg_verifybackup")), [destinationDirectory], null, ct).ConfigureAwait(false);
        if (verify.ExitCode != 0)
            throw new ProcessFailedException("pg_verifybackup", verify.ExitCode, verify.StandardError);

        var startWal = BaseBackupParsing.ReadStartWal(destinationDirectory)
            ?? throw new InvalidDataException("backup_label has no START WAL LOCATION line.");
        return new BackupSet(label, destinationDirectory, takenAt, BaseBackupParsing.SizeOf(destinationDirectory), startWal);
    }

    private static string ExeName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;
}
