using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PgUpgradePlannerTests
{
    private static readonly BundleLayout Layout = new(Path.Combine("install", "pg"), Path.Combine("data", "root"));

    [Fact]
    public void Check_run_uses_both_bindirs_and_datadirs_and_adds_check()
    {
        var command = PgUpgradePlanner.Plan(Layout, 18, 19, checkOnly: true, clusterRunning: false);

        Assert.Contains("--check", command.Arguments);
        Assert.Equal(Layout.Resolve(18).Bin, Argument(command.Arguments, "--old-bindir"));
        Assert.Equal(Layout.Resolve(19).Bin, Argument(command.Arguments, "--new-bindir"));
        Assert.Equal(Layout.Resolve(18).DataDirectory, Argument(command.Arguments, "--old-datadir"));
        Assert.Equal(Layout.Resolve(19).DataDirectory, Argument(command.Arguments, "--new-datadir"));
        Assert.Equal("pg_upgrade", Path.GetFileNameWithoutExtension(command.FileName));
    }

    [Fact]
    public void Real_upgrade_omits_check()
    {
        var command = PgUpgradePlanner.Plan(Layout, 18, 19, checkOnly: false, clusterRunning: false);

        Assert.DoesNotContain("--check", command.Arguments);
    }

    [Fact]
    public void Refuses_a_running_cluster()
    {
        Assert.Throws<InvalidOperationException>(() => PgUpgradePlanner.Plan(Layout, 18, 19, checkOnly: true, clusterRunning: true));
    }

    [Fact]
    public void Refuses_downgrades_and_same_major()
    {
        Assert.ThrowsAny<Exception>(() => PgUpgradePlanner.Plan(Layout, 19, 18, checkOnly: true, clusterRunning: false));
        Assert.ThrowsAny<Exception>(() => PgUpgradePlanner.Plan(Layout, 18, 18, checkOnly: true, clusterRunning: false));
    }

    [Fact]
    public void Refuses_a_major_the_bundle_does_not_ship()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PgUpgradePlanner.Plan(new BundleLayout("pg", "d"), 17, 18, checkOnly: true, clusterRunning: false));
    }

    [Fact]
    public void Refuses_a_jump_of_more_than_one_major()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PgUpgradePlanner.Plan(new BundleLayout("pg", "d"), 18, 20, checkOnly: true, clusterRunning: false));
    }

    [Fact]
    public void Dump_is_custom_format_and_never_carries_a_password()
    {
        var command = PgDumpBackupPlanner.PlanDump(Layout, 18, "backup.dump", 54320, "bazaar_admin", "bazaar");

        Assert.Contains("-Fc", command.Arguments);
        Assert.Contains("--no-password", command.Arguments);
        Assert.DoesNotContain(command.Arguments, arg => arg.Contains("password", StringComparison.OrdinalIgnoreCase) && arg != "--no-password");
        Assert.Equal("bazaar", command.Arguments[^1]);
    }

    [Fact]
    public void Restore_check_lists_the_dump_without_restoring_it()
    {
        var command = PgDumpBackupPlanner.PlanRestoreCheck(Layout, 18, "backup.dump");

        Assert.Equal(["--list", "backup.dump"], command.Arguments);
        Assert.Equal("pg_restore", Path.GetFileNameWithoutExtension(command.FileName));
    }

    private static string Argument(IReadOnlyList<string> arguments, string name) => arguments[IndexOf(arguments, name) + 1];

    private static int IndexOf(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Argument {name} not found.");
    }
}
