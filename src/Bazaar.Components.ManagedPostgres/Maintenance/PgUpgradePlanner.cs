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
    public static PgUpgradeCommand Plan(BundleLayout layout, int fromMajor, int toMajor, bool checkOnly, bool clusterRunning)
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
            "--link",
        };

        if (checkOnly)
        {
            arguments.Add("--check");
        }

        return new PgUpgradeCommand(Path.Combine(to.Bin, ExecutableName("pg_upgrade")), arguments);
    }

    private static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;
}
