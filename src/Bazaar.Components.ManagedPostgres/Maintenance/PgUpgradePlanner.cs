using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>A pg_upgrade invocation, ready to hand to <see cref="IPostgresCommandRunner"/>.</summary>
public sealed record PgUpgradeCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Builds the pg_upgrade invocation for moving a cluster from one supported major to the next (packaging.md P9).
/// It refuses downgrades, same-major runs, unsupported majors, and a cluster that is still running. A check run
/// (<c>--check</c>) always goes first; the real upgrade is only built when the caller asks for it.
/// </summary>
public static class PgUpgradePlanner
{
    /// <param name="superuser">
    /// The install (superuser) role of both clusters. pg_upgrade otherwise connects as the operating-system user.
    /// </param>
    /// <param name="link">
    /// False (the default) copies the data files, so the old cluster stays intact and is the rollback path. True hard-links
    /// them: faster, but the old cluster can no longer be started once the upgrade has run.
    /// </param>
    public static PgUpgradeCommand Plan(
        BundleLayout layout,
        int fromMajor,
        int toMajor,
        bool checkOnly,
        bool clusterRunning,
        bool link = false,
        string? superuser = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!BundleLayout.SupportedMajors.Contains(fromMajor) || !BundleLayout.SupportedMajors.Contains(toMajor))
        {
            throw new ArgumentOutOfRangeException(nameof(toMajor), toMajor, "Both majors must be supported by the bundle.");
        }

        if (toMajor != fromMajor + 1)
        {
            throw new InvalidOperationException(
                $"pg_upgrade moves one major at a time. Upgrade {fromMajor} to {fromMajor + 1} first, then continue.");
        }

        if (clusterRunning)
        {
            throw new InvalidOperationException("Stop the managed cluster before running pg_upgrade.");
        }

        var from = layout.Resolve(fromMajor);
        var to = layout.Resolve(toMajor);

        var arguments = new List<string>
        {
            "--old-bindir", from.Bin,
            "--new-bindir", to.Bin,
            "--old-datadir", from.DataDirectory,
            "--new-datadir", to.DataDirectory,
        };

        if (superuser is not null)
        {
            arguments.Add("-U");
            arguments.Add(superuser);
        }

        if (link)
        {
            arguments.Add("--link");
        }

        if (checkOnly)
        {
            arguments.Add("--check");
        }

        return new PgUpgradeCommand(Path.Combine(to.Bin, ExecutableName("pg_upgrade")), arguments);
    }

    private static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;
}
