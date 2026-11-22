using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>The validate command: exit codes, the printed issues and the plan hash (doc 45 section 1.7).</summary>
public sealed class ValidateCommandTests
{
    [Fact]
    public async Task Valid_file_exits_ok_and_prints_a_64_character_plan_hash()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "validate", "--config", config);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("Result: valid, with no errors or holds (exit 0)", run.Stdout);
        var hash = AdminFixtures.HashFrom(run.Stdout);
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        Assert.Empty(run.Stderr);
    }

    [Fact]
    public async Task Invalid_file_exits_invalid_input_and_names_the_key_and_rule_only()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("setup", "validate", "--config", config);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at install.releaseChannel: must be alpha, beta, stable, ltsc or latest-commit.", run.Stdout);
        Assert.Contains("Plan hash: not computed, because the file has errors.", run.Stdout);
        Assert.DoesNotContain("nightly", run.All);
    }

    [Fact]
    public async Task Held_file_exits_held_and_still_prints_the_hash()
    {
        using var folder = new TempSetupFolder();
        // No backup target and no acknowledgement is a hold (SetupConstants.MissingBackupTargetSeverity).
        var config = folder.Write(AdminFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"));

        var run = await AdminRun.Async("setup", "validate", "--config", config);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Contains("Hold at ", run.Stdout);
        Assert.Equal(64, AdminFixtures.HashFrom(run.Stdout).Length);
    }

    [Fact]
    public async Task Json_output_is_one_document_with_the_expected_fields()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "validate", "--config", config, "--json");

        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("validate", root.GetProperty("command").GetString());
        Assert.Equal((int)SetupExitCode.Ok, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("valid", root.GetProperty("result").GetString());
        Assert.Equal(64, root.GetProperty("planHash").GetString()!.Length);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("issues").ValueKind);
        Assert.Equal(0, root.GetProperty("changes").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentState").ValueKind);
    }

    [Fact]
    public async Task Json_invalid_file_lists_issues_with_path_and_message()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("setup", "validate", "--config", config, "--json");

        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal((int)SetupExitCode.InvalidInput, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("planHash").ValueKind);
        var issue = root.GetProperty("issues")[0];
        Assert.Equal("error", issue.GetProperty("severity").GetString());
        Assert.Equal("install.releaseChannel", issue.GetProperty("path").GetString());
        Assert.DoesNotContain("nightly", run.All);
    }
}

/// <summary>The dry run: the change list for an empty install, the hash, and no writes (doc 45 section 1.7).</summary>
public sealed class DryRunCommandTests
{
    [Fact]
    public async Task Empty_install_lists_creates_only_and_writes_nothing()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "--dry-run", "--config", config);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("Create install.companyName = \"Example Studio\"", run.Stdout);
        Assert.Contains("Create install.companySlug = \"example-studio\"", run.Stdout);
        Assert.Contains("Create settings.host.shutdown_timeout_seconds = 20", run.Stdout);
        Assert.DoesNotContain("\nUpdate ", run.Stdout);
        Assert.DoesNotContain("\nRemove ", run.Stdout);
        Assert.Contains("Writes: none.", run.Stdout);
        Assert.Contains("Current state: empty install.", run.Stdout);
        Assert.Equal(64, AdminFixtures.HashFrom(run.Stdout).Length);
    }

    [Fact]
    public async Task Dry_run_never_prints_a_secret_reference_value()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "--dry-run", "--config", config);

        // Each reference value from the fixture is absent, in both streams.
        Assert.DoesNotContain("secret://bazaar/owner/app-id", run.All);
        Assert.DoesNotContain("secret://bazaar/providers/", run.All);
        Assert.DoesNotContain("kek.bin", run.All);
        Assert.DoesNotContain("dpapi:C:", run.All);
        // The scheme and the count are shown instead.
        Assert.Contains("Create custody.kekRef = (reference with scheme dpapi:, value not shown)", run.Stdout);
        Assert.Contains("Create models.sources = (list of 2 entries; key references not shown)", run.Stdout);
    }

    [Fact]
    public async Task Dry_run_json_lists_each_change_with_kind_and_path()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await AdminRun.Async("setup", "--dry-run", "--config", config, "--json");

        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("dry-run", root.GetProperty("command").GetString());
        Assert.Equal("empty", root.GetProperty("currentState").GetString());
        var changes = root.GetProperty("changes");
        Assert.True(changes.GetArrayLength() > 0);
        Assert.All(changes.EnumerateArray(), change => Assert.Equal("create", change.GetProperty("kind").GetString()));

        var companyName = changes.EnumerateArray().Single(c => c.GetProperty("path").GetString() == "install.companyName");
        Assert.Equal("\"Example Studio\"", companyName.GetProperty("planned").GetString());
        Assert.Equal(JsonValueKind.Null, companyName.GetProperty("current").ValueKind);
    }

    [Fact]
    public async Task Held_plan_still_lists_its_changes_and_exits_held()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null"));

        var run = await AdminRun.Async("setup", "--dry-run", "--config", config);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Contains("Hold at ", run.Stdout);
        Assert.Contains("Create install.companyName", run.Stdout);
        Assert.Contains("Writes: none.", run.Stdout);
    }

    [Fact]
    public async Task Invalid_file_lists_no_changes()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"nightly\""));

        var run = await AdminRun.Async("setup", "--dry-run", "--config", config);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Changes: not computed.", run.Stdout);
        Assert.DoesNotContain("Create ", run.Stdout);
    }
}
