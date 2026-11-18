using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>The hand-written parser: unknown commands, missing and misplaced flags, and the help text.</summary>
public sealed class ArgumentAndHelpTests
{
    [Fact]
    public async Task No_arguments_exits_invalid_input()
    {
        var run = await AdminRun.Async();

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("no command given", run.Stderr);
        Assert.Empty(run.Stdout);
    }

    [Fact]
    public async Task Unknown_command_exits_invalid_input_and_does_not_echo_the_word()
    {
        var run = await AdminRun.Async("frobnicate", "--config", "x");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("unknown command", run.Stderr);
        Assert.DoesNotContain("frobnicate", run.All);
    }

    [Fact]
    public async Task Setup_without_a_subcommand_or_dry_run_exits_invalid_input()
    {
        var run = await AdminRun.Async("setup");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("needs validate, apply, or --dry-run", run.Stderr);
    }

    [Fact]
    public async Task Unknown_setup_subcommand_exits_invalid_input()
    {
        var run = await AdminRun.Async("setup", "secret", "--config", "x");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("unknown setup command", run.Stderr);
    }

    [Fact]
    public async Task Missing_config_names_the_flag_for_every_form()
    {
        foreach (var args in new[]
                 {
                     new[] { "setup", "validate" },
                     new[] { "setup", "--dry-run" },
                     new[] { "setup", "apply", "--plan-hash", new string('a', 64) },
                 })
        {
            var run = await AdminRun.Async(args);

            Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
            Assert.Contains("Error at --config: is required", run.Stderr);
        }
    }

    [Fact]
    public async Task Config_flag_without_a_value_names_the_flag()
    {
        var run = await AdminRun.Async("setup", "validate", "--config");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --config: needs a value", run.Stderr);
    }

    [Fact]
    public async Task Apply_without_plan_hash_names_the_flag()
    {
        var run = await AdminRun.Async("setup", "apply", "--config", "x");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --plan-hash: is required for apply", run.Stderr);
    }

    [Fact]
    public async Task Malformed_plan_hash_names_the_flag_and_does_not_echo_it()
    {
        var run = await AdminRun.Async("setup", "apply", "--config", "x", "--plan-hash", "not-a-hash-value");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --plan-hash: must be the 64-character", run.Stderr);
        Assert.DoesNotContain("not-a-hash-value", run.All);
    }

    [Theory]
    [InlineData("validate", "--plan-hash")]
    [InlineData("validate", "--dry-run")]
    [InlineData("apply", "--dry-run")]
    public async Task Flags_used_in_the_wrong_form_are_refused(string subcommand, string flag)
    {
        var args = new List<string> { "setup", subcommand, "--config", "x" };
        args.Add(flag);
        if (flag == "--plan-hash")
        {
            args.Add(new string('a', 64));
        }

        var run = await AdminRun.Async([.. args]);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains(flag, run.Stderr);
    }

    [Fact]
    public async Task Unknown_option_is_named_but_a_non_option_token_is_not_echoed()
    {
        var unknown = await AdminRun.Async("setup", "validate", "--config", "x", "--bogus-flag");
        Assert.Equal((int)SetupExitCode.InvalidInput, unknown.ExitCode);
        Assert.Contains("Error at --bogus-flag: is not an option", unknown.Stderr);

        var stray = await AdminRun.Async("setup", "validate", "--config", "x", "stray-token-7");
        Assert.Equal((int)SetupExitCode.InvalidInput, stray.ExitCode);
        Assert.Contains("Error at argument: is not an option. Options start with two dashes", stray.Stderr);
        Assert.DoesNotContain("stray-token-7", stray.All);
    }

    [Fact]
    public async Task Missing_file_names_the_config_flag_and_exits_invalid_input()
    {
        using var folder = new TempSetupFolder();

        var run = await AdminRun.Async("setup", "validate", "--config", folder.PathOf("does-not-exist.json"));

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --config: the file cannot be read", run.Stdout);
    }

    [Fact]
    public async Task Inline_equals_form_is_accepted_for_config()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "validate", "--config=" + config);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
    }

    [Fact]
    public async Task Usage_error_with_json_answers_in_one_json_document_on_stdout()
    {
        var run = await AdminRun.Async("setup", "validate", "--json");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Empty(run.Stderr);
        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("usage", root.GetProperty("command").GetString());
        Assert.Equal((int)SetupExitCode.InvalidInput, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("--config", root.GetProperty("issues")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Top_help_exits_ok_and_lists_the_three_setup_forms()
    {
        var run = await AdminRun.Async("--help");

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("setup validate --config <path>", run.Stdout);
        Assert.Contains("setup --dry-run --config <path>", run.Stdout);
        Assert.Contains("setup apply --config <path> --plan-hash <hash>", run.Stdout);
        Assert.Contains($"{(int)SetupExitCode.Held}  Held", run.Stdout);
    }

    [Fact]
    public async Task Setup_help_exits_ok_and_says_the_state_reader_is_not_built_yet()
    {
        var run = await AdminRun.Async("setup", "--help");

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("--plan-hash <hash>", run.Stdout);
        Assert.Contains("compares against an empty install only", run.Stdout);
        Assert.Contains("placeholders in this build", run.Stdout);
    }
}
