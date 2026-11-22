using System.Text.Json;
using Bazaar.Admin;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>The <c>kodo verify</c> command: exit codes, the text and JSON shape, usage errors and the help text (doc 45 section 1.12).</summary>
public sealed class KodoVerifyTests
{
    private const int _placeholderCount = 15;

    private static readonly string[] _pureCheckIds =
    [
        "network.statusBind", "backups.target", "owner.secondFactor", "secrets.syntax", "audit.anchorTarget", "identity.kekCustody",
    ];

    [Fact]
    public async Task Valid_file_exits_amber_because_the_placeholders_are_not_evaluated()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("kodo", "verify", "--config", config);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Equal(KodoVerifyExitCodes.Amber, run.ExitCode);
        Assert.Contains("Command: kodo verify", run.Stdout);
        Assert.Contains("Result: amber, held for acknowledgement (exit 2)", run.Stdout);
        Assert.Contains("Score: 25 of 100", run.Stdout);
        Assert.Contains($"Amber items needing acknowledgement: {_placeholderCount}.", run.Stdout);
        Assert.Contains("Amber item schema.migrations: Schema version and migration state", run.Stdout);
        Assert.Contains("Writes: none.", run.Stdout);
        Assert.Empty(run.Stderr);
    }

    [Fact]
    public async Task Text_output_has_one_line_per_check_with_its_status_and_message()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("kodo", "verify", "--config", config);

        var lines = run.Stdout.Split('\n');
        foreach (var id in _pureCheckIds)
        {
            Assert.Contains(lines, line => line.StartsWith($"Check {id} (", StringComparison.Ordinal) && line.Contains("): green.", StringComparison.Ordinal));
        }

        Assert.Contains(lines, line => line.StartsWith("Check instance.lock (Instance lock): not evaluated. Not evaluated: needs the Host adapter.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_red_check_exits_three_and_names_the_remediation()
    {
        using var folder = new TempSetupFolder();
        // No target and no acknowledgement: the validator holds it, and the backup check is red.
        var config = folder.Write(AdminFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"));

        var run = await AdminRun.Async("kodo", "verify", "--config", config);

        Assert.Equal(KodoVerifyExitCodes.Red, run.ExitCode);
        Assert.Contains("Result: red, setup cannot be marked complete (exit 3)", run.Stdout);
        Assert.Contains("Check backups.target (Backup target configured or acknowledged): red.", run.Stdout);
        Assert.Contains("Remediation for backups.target:", run.Stdout);
        Assert.Contains("Hold at backups.target:", run.Stdout);
    }

    [Fact]
    public async Task Invalid_file_exits_one_with_no_checks_and_no_score()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("kodo", "verify", "--config", config);

        Assert.Equal(KodoVerifyExitCodes.InvalidInput, run.ExitCode);
        Assert.Contains("Result: invalid input (exit 1)", run.Stdout);
        Assert.Contains("Score: not computed, because the file has errors.", run.Stdout);
        Assert.DoesNotContain("Check ", run.Stdout);
        Assert.Contains("Error at install.releaseChannel:", run.Stdout);
    }

    [Fact]
    public async Task Unreadable_file_exits_one_and_does_not_echo_the_path()
    {
        var run = await AdminRun.Async("kodo", "verify", "--config", "/nonexistent-folder-for-test/bazaar-setup.json");

        Assert.Equal(KodoVerifyExitCodes.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --config: the file cannot be read", run.Stdout);
        Assert.DoesNotContain("nonexistent-folder-for-test", run.All);
    }

    [Fact]
    public async Task Json_output_is_one_document_with_the_checks_the_score_and_the_amber_items()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("kodo", "verify", "--config", config, "--json");

        Assert.Equal(KodoVerifyExitCodes.Amber, run.ExitCode);
        Assert.Empty(run.Stderr);
        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("verify", root.GetProperty("command").GetString());
        Assert.Equal(KodoVerifyExitCodes.Amber, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("amber", root.GetProperty("result").GetString());
        Assert.Equal(25, root.GetProperty("score").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("planHash").GetString()!);

        var checks = root.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal(_pureCheckIds.Length + _placeholderCount, checks.Count);
        Assert.Equal("green", checks.Single(check => check.GetProperty("id").GetString() == "backups.target").GetProperty("status").GetString());
        var placeholder = checks.Single(check => check.GetProperty("id").GetString() == "instance.lock");
        Assert.Equal("not-evaluated", placeholder.GetProperty("status").GetString());

        var amber = root.GetProperty("amberItems").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(_placeholderCount, amber.Count);
        Assert.Equal("schema.migrations", amber[0]);
        Assert.Empty(root.GetProperty("issues").EnumerateArray());
    }

    [Fact]
    public async Task Json_output_for_an_invalid_file_has_no_checks_and_a_null_score()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("kodo", "verify", "--config", config, "--json");

        Assert.Equal(KodoVerifyExitCodes.InvalidInput, run.ExitCode);
        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("invalid-input", root.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("score").ValueKind);
        Assert.Empty(root.GetProperty("checks").EnumerateArray());
    }

    [Theory]
    [InlineData("kekRef")]
    [InlineData("keyRef")]
    [InlineData("appIdRef")]
    public async Task A_canary_in_a_secret_key_appears_in_no_verify_output(string variant)
    {
        var text = variant switch
        {
            "kekRef" => AdminFixtures.With("\"kekRef\": \"dpapi:C:\\\\ProgramData\\\\Bazaar\\\\kek.bin\"", $"\"kekRef\": \"{AdminFixtures.Canary}\""),
            "keyRef" => AdminFixtures.With("\"secret://bazaar/providers/freellmapi/key\"", $"\"{AdminFixtures.Canary}\""),
            _ => AdminFixtures.With("\"secret://bazaar/owner/app-id\"", $"\"{AdminFixtures.Canary}\""),
        };
        using var folder = new TempSetupFolder();
        var config = folder.Write(text);

        var textRun = await AdminRun.Async("kodo", "verify", "--config", config);
        var jsonRun = await AdminRun.Async("kodo", "verify", "--config", config, "--json");

        Assert.DoesNotContain(AdminFixtures.Canary, textRun.All);
        Assert.DoesNotContain(AdminFixtures.Canary, jsonRun.All);
        Assert.Equal(KodoVerifyExitCodes.InvalidInput, textRun.ExitCode);
    }

    [Fact]
    public void Exit_codes_map_green_amber_and_red_and_a_validator_hold_to_amber()
    {
        Assert.Equal(0, KodoVerifyExitCodes.Green);
        Assert.Equal(1, KodoVerifyExitCodes.InvalidInput);
        Assert.Equal(2, KodoVerifyExitCodes.Amber);
        Assert.Equal(3, KodoVerifyExitCodes.Red);

        Assert.Equal(KodoVerifyExitCodes.Green, KodoVerifyExitCodes.For(SetupCheckStatus.Green, hasValidatorHold: false));
        Assert.Equal(KodoVerifyExitCodes.Amber, KodoVerifyExitCodes.For(SetupCheckStatus.Green, hasValidatorHold: true));
        Assert.Equal(KodoVerifyExitCodes.Amber, KodoVerifyExitCodes.For(SetupCheckStatus.Amber, hasValidatorHold: false));
        Assert.Equal(KodoVerifyExitCodes.Amber, KodoVerifyExitCodes.For(SetupCheckStatus.NotEvaluated, hasValidatorHold: false));
        Assert.Equal(KodoVerifyExitCodes.Red, KodoVerifyExitCodes.For(SetupCheckStatus.Red, hasValidatorHold: false));
        Assert.Equal(KodoVerifyExitCodes.Red, KodoVerifyExitCodes.For(SetupCheckStatus.Red, hasValidatorHold: true));
    }

    [Fact]
    public async Task Kodo_without_a_verb_is_a_usage_error_that_does_not_echo_the_word()
    {
        var run = await AdminRun.Async("kodo");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("needs verify", run.Stderr);
    }

    [Fact]
    public async Task Unknown_kodo_verb_is_refused_without_echoing_it()
    {
        var run = await AdminRun.Async("kodo", "repair", "--config", "x");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("unknown kodo command", run.Stderr);
        Assert.DoesNotContain("repair", run.All);
    }

    [Fact]
    public async Task Verify_without_config_names_the_flag()
    {
        var run = await AdminRun.Async("kodo", "verify");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --config: is required", run.Stderr);
    }

    [Theory]
    [InlineData("--dry-run")]
    public async Task Setup_only_switches_are_refused_by_kodo_verify(string flag)
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("kodo", "verify", "--config", config, flag);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("is not used with kodo verify", run.Stderr);
        Assert.DoesNotContain("Check ", run.Stdout);
    }

    [Fact]
    public async Task Plan_hash_is_refused_by_kodo_verify_so_it_can_never_reach_apply()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("kodo", "verify", "--config", config, "--plan-hash", new string('0', 64));

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("is only for setup apply", run.Stderr);
    }

    [Fact]
    public async Task Kodo_help_lists_the_verify_form_and_the_exit_codes_with_the_red_meaning()
    {
        var run = await AdminRun.Async("kodo", "--help");

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("kodo verify --config <path> [--json]", run.Stdout);
        Assert.Contains($"{KodoVerifyExitCodes.Green}  Green", run.Stdout);
        Assert.Contains($"{KodoVerifyExitCodes.Amber}  Amber", run.Stdout);
        Assert.Contains($"{KodoVerifyExitCodes.Red}  Red", run.Stdout);
        Assert.Contains("not evaluated", run.Stdout);
    }

    [Fact]
    public async Task Top_help_lists_the_kodo_command()
    {
        var run = await AdminRun.Async("--help");

        Assert.Contains("kodo verify --config <path> [--json]", run.Stdout);
        Assert.Contains("kodo     Verify a finished setup", run.Stdout);
    }
}
