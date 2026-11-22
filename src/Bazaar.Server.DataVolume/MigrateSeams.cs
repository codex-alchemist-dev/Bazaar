using System.Diagnostics;
using System.Globalization;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// Tells <c>storage migrate</c> whether the host is still using the old data root (doc 63 section 10: "stop the host").
/// The host's own instance lock is a PostgreSQL advisory lock, which needs a live database connection and is not reachable
/// from this module, so the shipped probe looks at the cluster's <c>postmaster.pid</c> instead.
/// </summary>
public interface IHostRunningProbe
{
    /// <summary>Returns why the old data root is in use, or null when nothing indicates that it is.</summary>
    ValueTask<string?> FindInUseAsync(string oldDataRoot, CancellationToken cancellationToken);
}

/// <summary>
/// Refuses while any cluster under <c>pg/&lt;major&gt;/data</c> has a <c>postmaster.pid</c>. A clean PostgreSQL shutdown removes it,
/// so a file that is left behind is treated as in use too (fail closed): the operator checks that no postgres process runs and
/// removes the stale file by hand. This keeps a stale lock file from being copied into the new cluster as well.
/// </summary>
public sealed class PostmasterPidHostRunningProbe : IHostRunningProbe
{
    public ValueTask<string?> FindInUseAsync(string oldDataRoot, CancellationToken cancellationToken)
    {
        var pgRoot = Path.Combine(oldDataRoot, "pg");
        if (Directory.Exists(pgRoot))
        {
            foreach (var major in Directory.EnumerateDirectories(pgRoot))
            {
                var pidFile = Path.Combine(major, "data", "postmaster.pid");
                if (File.Exists(pidFile))
                    return ValueTask.FromResult<string?>($"'{pidFile}' exists: a PostgreSQL server may still be running on the old data root. Stop the host first; if the server is stopped and the file is stale, remove it by hand.");
            }
        }

        return ValueTask.FromResult<string?>(null);
    }
}

/// <summary>How a <c>pg_checksums --check</c> run ended.</summary>
public enum PostgresChecksumOutcome
{
    /// <summary>Every page checksum matched.</summary>
    Passed,

    /// <summary>A checksum did not match, or the tool failed.</summary>
    Failed,

    /// <summary>The cluster was created without data checksums, so the tool has nothing to check.</summary>
    ChecksumsDisabled,

    /// <summary>The tool could not be found or started on this host.</summary>
    ToolUnavailable,
}

/// <param name="Outcome">The result.</param>
/// <param name="Detail">One line for the operator; never key material.</param>
public sealed record PostgresChecksumResult(PostgresChecksumOutcome Outcome, string Detail);

/// <summary>Runs <c>pg_checksums --check</c> on a stopped cluster (doc 63 section 10).</summary>
public interface IPostgresChecksumVerifier
{
    ValueTask<PostgresChecksumResult> CheckAsync(string clusterDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// The seam for the secure removal of the plain copy (doc 61's step). It is declared here and has no implementation in this
/// build: <c>storage migrate</c> does not call it and ends with the plain copy kept.
/// </summary>
public interface IPlainCopyRemover
{
    ValueTask RemoveAsync(string plainDataRoot, CancellationToken cancellationToken);
}

/// <summary>The collaborators <c>storage migrate</c> needs beyond those of <c>storage init</c>.</summary>
public sealed record StorageMigrateServices(IHostRunningProbe HostProbe, IPostgresChecksumVerifier ChecksumVerifier)
{
    /// <summary>Test seam: called at the start ("start:&lt;phase&gt;") and end ("done:&lt;phase&gt;", after the journal is written) of each phase. Throwing simulates a crash.</summary>
    internal Action<string>? Checkpoint { get; init; }
}

/// <summary>
/// Runs the bundled <c>pg_checksums</c> from <c>&lt;bundle root&gt;/&lt;major&gt;/bin</c> (the layout of the managed PostgreSQL bundle),
/// where the major comes from the cluster's <c>PG_VERSION</c>. A missing tool or bundle is reported as unavailable, never as a pass.
/// </summary>
public sealed class PgChecksumsProcessVerifier(string? bundleRoot) : IPostgresChecksumVerifier
{
    public async ValueTask<PostgresChecksumResult> CheckAsync(string clusterDirectory, CancellationToken cancellationToken)
    {
        var tool = LocateTool(clusterDirectory);
        if (tool is null)
            return new(PostgresChecksumOutcome.ToolUnavailable, "pg_checksums was not found in the PostgreSQL bundle.");

        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--check");
        info.ArgumentList.Add("-D");
        info.ArgumentList.Add(clusterDirectory);
        info.Environment["LC_ALL"] = "C"; // The "not enabled" message is matched below.

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var text = (await stderr + " " + await stdout).ReplaceLineEndings(" ").Trim();
            if (process.ExitCode == 0)
                return new(PostgresChecksumOutcome.Passed, "pg_checksums --check: no checksum mismatch.");

            return text.Contains("checksums are not enabled", StringComparison.OrdinalIgnoreCase)
                ? new(PostgresChecksumOutcome.ChecksumsDisabled, "the cluster has data checksums disabled.")
                : new(PostgresChecksumOutcome.Failed, $"pg_checksums exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {text}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(PostgresChecksumOutcome.ToolUnavailable, $"pg_checksums could not be started: {e.Message}");
        }
    }

    private string? LocateTool(string clusterDirectory)
    {
        if (string.IsNullOrWhiteSpace(bundleRoot))
            return null;

        string major;
        try
        {
            major = File.ReadLines(Path.Combine(clusterDirectory, "PG_VERSION")).FirstOrDefault()?.Trim() ?? string.Empty;
        }
        catch (IOException)
        {
            return null;
        }

        if (major.Length == 0 || major.Any(c => !char.IsAsciiDigit(c)))
            return null;

        var path = Path.Combine(bundleRoot, major, "bin", OperatingSystem.IsWindows() ? "pg_checksums.exe" : "pg_checksums");
        return File.Exists(path) ? path : null;
    }
}
