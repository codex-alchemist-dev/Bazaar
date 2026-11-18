using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>The apply command. No steps are built yet, so a plan with changes fails with exit 3 and writes nothing.</summary>
public sealed class ApplyCommandTests
{
    [Fact]
    public async Task Hash_mismatch_exits_invalid_input_and_names_the_plan_hash_field()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", new string('0', 64));

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at planHash: does not match the plan; run the dry run again", run.Stdout);
        Assert.Contains("Applied steps: none.", run.Stdout);
    }

    [Fact]
    public async Task Plan_with_changes_and_no_steps_exits_apply_failed_and_says_so_plainly()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash);

        Assert.Equal((int)SetupExitCode.ApplyFailedRolledBack, run.ExitCode);
        Assert.Contains("Result: apply failed and was rolled back (exit 3)", run.Stdout);
        Assert.Contains("no step implementation for ", run.Stdout);
        Assert.Contains("Applied steps: none.", run.Stdout);
        Assert.Contains("Plan hash: " + hash, run.Stdout);
    }

    [Fact]
    public async Task Held_plan_with_the_right_hash_exits_held_and_changes_nothing()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"));
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Contains("Result: held, so nothing was changed (exit 2)", run.Stdout);
        Assert.DoesNotContain("no step implementation", run.All);
    }

    [Fact]
    public async Task Invalid_file_exits_invalid_input_before_the_hash_is_checked()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", new string('a', 64));

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at install.releaseChannel", run.Stdout);
    }

    [Fact]
    public async Task Json_apply_failure_reports_exit_3_and_no_applied_steps()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await AdminRun.Async("setup", "validate", "--config", config)).Stdout);

        var run = await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", hash, "--json");

        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("apply", root.GetProperty("command").GetString());
        Assert.Equal((int)SetupExitCode.ApplyFailedRolledBack, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("apply-failed-rolled-back", root.GetProperty("result").GetString());
        Assert.Equal(0, root.GetProperty("appliedSteps").GetArrayLength());
        Assert.Equal(hash, root.GetProperty("planHash").GetString());
    }
}
