using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Admin;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>An environment for one run: the variables the test names, and nothing from the process.</summary>
internal sealed class DictionarySetupEnvironment(IReadOnlyDictionary<string, string?> variables) : ISetupEnvironment
{
    public IReadOnlyDictionary<string, string?> Variables { get; } = variables;
}

/// <summary>
/// The environment and flag doors through the command line (doc 45 section 1.2): <c>--set</c> repeats, the optional file, the
/// equivalence of the three doors, the apply entry path, and the canary that must reach no output.
/// </summary>
public sealed class SetupDoorsCliTests
{
    private static async Task<CliRun> Run(IReadOnlyDictionary<string, string?>? environment, ICliApplyGate? gate, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AdminCommandLine.RunAsync(
            args,
            stdout,
            stderr,
            applyGate: gate,
            environment: new DictionarySetupEnvironment(environment ?? new Dictionary<string, string?>(StringComparer.Ordinal)),
            cancellationToken: TestContext.Current.CancellationToken);
        return new CliRun(code, stdout.ToString(), stderr.ToString());
    }

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] variables)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// The fixture's leaves as the same plan written for the other two doors: each leaf once as an environment variable and once as
    /// a <c>--set</c> flag. Every key of the plan is covered, so the equivalence is not a partial one.
    /// </summary>
    private static (Dictionary<string, string?> Environment, List<string> Flags) PlanAsEnvironmentAndFlags()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new List<string>();
        foreach (var (path, text) in SetupPlanCanonicalizer.Leaves(JsonNode.Parse(AdminFixtures.Valid)).Where(leaf => leaf.Value != "{}"))
        {
            var value = JsonNode.Parse(text) is JsonValue scalar && scalar.TryGetValue<string>(out var raw) ? raw : text;
            environment["BAZAAR__SETUP__" + string.Join("__", path.Split('.')).ToUpperInvariant()] = value;
            flags.Add(path + "=" + value);
        }

        return (environment, flags);
    }

    /// <summary>The flags as the command line spells them: one <c>--set</c> before each value.</summary>
    private static IEnumerable<string> AsSetArguments(IEnumerable<string> flags) => flags.SelectMany(flag => new[] { "--set", flag });

    [Fact]
    public async Task The_same_plan_through_the_file_the_environment_and_the_flags_gives_one_plan_hash()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var (environment, flags) = PlanAsEnvironmentAndFlags();

        var fromFile = await Run(null, null, "setup", "--dry-run", "--config", config);
        var fromEnvironment = await Run(environment, null, "setup", "--dry-run");
        var fromFlags = await Run(Env(), null, ["setup", "--dry-run", .. AsSetArguments(flags)]);

        Assert.Equal((int)SetupExitCode.Ok, fromFile.ExitCode);
        Assert.Equal((int)SetupExitCode.Ok, fromEnvironment.ExitCode);
        Assert.Equal((int)SetupExitCode.Ok, fromFlags.ExitCode);
        var hash = AdminFixtures.HashFrom(fromFile.Stdout);
        Assert.Equal(hash, AdminFixtures.HashFrom(fromEnvironment.Stdout));
        Assert.Equal(hash, AdminFixtures.HashFrom(fromFlags.Stdout));
    }

    [Fact]
    public async Task An_environment_only_plan_reaches_the_applier_with_the_same_hash_as_the_file()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var hash = AdminFixtures.HashFrom((await Run(null, null, "setup", "validate", "--config", config)).Stdout);
        var (environment, _) = PlanAsEnvironmentAndFlags();
        var gate = new CountingApplyGate(open: true);

        var run = await Run(environment, gate, "setup", "apply", "--plan-hash", hash);

        // The applier takes the lock, then fails the changes with no steps (exit 3), as the file path does.
        Assert.Equal((int)SetupExitCode.ApplyFailedRolledBack, run.ExitCode);
        Assert.Equal(1, gate.Lock.Acquisitions);
        Assert.Contains("Plan hash: " + hash, run.Stdout);
    }

    [Fact]
    public async Task Config_is_optional_when_a_flag_or_an_environment_variable_gives_the_values()
    {
        var flagOnly = await Run(null, null, "setup", "validate", "--set", "install.companyName=Studio");
        var nothing = await Run(null, null, "setup", "validate");

        // The plan starts empty, so the required keys are reported as errors. The usage error is not used for a missing file.
        Assert.Equal((int)SetupExitCode.InvalidInput, flagOnly.ExitCode);
        Assert.DoesNotContain("Error at install.companyName", flagOnly.Stdout);
        Assert.Contains("Error at schemaVersion: is required.", flagOnly.Stdout);
        Assert.Contains("Error at install.companySlug: is required.", flagOnly.Stdout);
        Assert.DoesNotContain("--config", flagOnly.All);
        Assert.Empty(flagOnly.Stderr);

        Assert.Equal((int)SetupExitCode.InvalidInput, nothing.ExitCode);
        Assert.Contains("Error at --config: is required unless --set or a BAZAAR__SETUP__ variable", nothing.Stderr);
    }

    [Fact]
    public async Task A_complete_plan_from_environment_alone_validates_without_a_file()
    {
        var (environment, _) = PlanAsEnvironmentAndFlags();

        var run = await Run(environment, null, "setup", "validate");

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("Result: valid", run.Stdout);
    }

    [Fact]
    public async Task Flags_win_over_the_file_and_the_environment_win_over_the_file()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var expected = AdminFixtures.HashFrom((await Run(null, null, "setup", "validate", "--config", config)).Stdout);
        var renamed = AdminFixtures.With("\"Example Studio\"", "\"Flag Studio\"");
        var renamedFile = folder.Write(renamed, "renamed.json");
        var expectedFlagged = AdminFixtures.HashFrom((await Run(null, null, "setup", "validate", "--config", renamedFile)).Stdout);

        var flagged = await Run(null, null, "setup", "validate", "--config", config, "--set", "install.companyName=Flag Studio");
        var fromEnvironment = await Run(
            Env(("BAZAAR__SETUP__INSTALL__COMPANYNAME", "Env Studio")),
            null,
            "setup", "validate", "--config", config);

        Assert.Equal(expectedFlagged, AdminFixtures.HashFrom(flagged.Stdout));
        Assert.NotEqual(expected, AdminFixtures.HashFrom(fromEnvironment.Stdout));
        Assert.Equal((int)SetupExitCode.Ok, fromEnvironment.ExitCode);
    }

    [Fact]
    public async Task The_set_flag_repeats_and_its_last_value_for_one_key_wins()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await Run(
            null,
            null,
            "setup", "--dry-run", "--config", config,
            "--set", "install.companyName=First", "--set=install.companyName=Second");

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("Create install.companyName = \"Second\"", run.Stdout);
        Assert.DoesNotContain("First", run.Stdout);
    }

    [Fact]
    public async Task Environment_names_outside_the_prefix_are_not_read()
    {
        var run = await Run(Env(("OTHER__SETUP__INSTALL__COMPANYNAME", "x"), ("PATH", "/usr/bin")), null, "setup", "validate");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --config: is required unless", run.Stderr);
    }

    [Fact]
    public async Task A_set_flag_without_an_equals_sign_is_a_usage_error_that_does_not_echo_its_text()
    {
        var run = await Run(null, null, "setup", "validate", "--set", AdminFixtures.Canary);
        var json = await Run(null, null, "setup", "validate", "--set", AdminFixtures.Canary, "--json");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --set: must be a path and a value joined by an equals sign", run.Stderr);
        Assert.DoesNotContain(AdminFixtures.Canary, run.All);
        Assert.DoesNotContain(AdminFixtures.Canary, json.All);
    }

    [Fact]
    public async Task A_set_flag_with_no_value_names_the_flag()
    {
        var run = await Run(null, null, "setup", "validate", "--set");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at --set: needs a value.", run.Stderr);
    }

    [Fact]
    public async Task A_door_error_is_a_json_report_with_the_key_path()
    {
        var run = await Run(null, null, "setup", "validate", "--set", "install.nope=x", "--json");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("invalid-input", root.GetProperty("result").GetString());
        Assert.Equal("install.nope", root.GetProperty("issues")[0].GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("planHash").ValueKind);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("flag")]
    [InlineData("both")]
    public async Task A_canary_in_a_secret_key_through_either_door_reaches_no_output_of_any_command(string door)
    {
        // The canary is the value of a bootstrap reference (custody.kekRef) and of a model key (models.sources[0].keyRef).
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new List<string>();
        if (door is "environment" or "both")
        {
            environment["BAZAAR__SETUP__CUSTODY__KEKREF"] = AdminFixtures.Canary;
            environment["BAZAAR__SETUP__MODELS__SOURCES__0__KEYREF"] = AdminFixtures.Canary;
        }

        if (door is "flag" or "both")
        {
            flags.Add("custody.kekRef=" + AdminFixtures.Canary);
            flags.Add("models.sources.0.keyRef=" + AdminFixtures.Canary);
        }

        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);
        var zeroHash = new string('0', 64);
        var gate = new CountingApplyGate(open: true);
        var sets = AsSetArguments(flags).ToArray();

        var runs = new[]
        {
            await Run(environment, null, ["setup", "validate", "--config", config, .. sets]),
            await Run(environment, null, ["setup", "validate", "--config", config, .. sets, "--json"]),
            await Run(environment, null, ["setup", "--dry-run", "--config", config, .. sets]),
            await Run(environment, null, ["setup", "--dry-run", "--config", config, .. sets, "--json"]),
            await Run(environment, null, ["setup", "apply", "--config", config, .. sets, "--plan-hash", zeroHash]),
            await Run(environment, null, ["setup", "apply", "--config", config, .. sets, "--plan-hash", zeroHash, "--json"]),
            await Run(environment, gate, ["setup", "apply", "--plan-hash", zeroHash]),
            await Run(environment, gate, ["setup", "apply", "--plan-hash", zeroHash, "--json"]),
        };

        foreach (var run in runs)
        {
            Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
            Assert.DoesNotContain(AdminFixtures.Canary, run.All);
        }

        Assert.Equal(0, gate.Lock.Acquisitions);
    }

    [Fact]
    public async Task A_non_numeric_environment_value_for_an_int_setting_names_the_key_and_not_the_value()
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(AdminFixtures.Valid);

        var run = await Run(
            Env(("BAZAAR__SETUP__SETTINGS__HOST__SHUTDOWN_TIMEOUT_SECONDS", AdminFixtures.Canary)),
            null,
            "setup", "--dry-run", "--config", config);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains("Error at settings.host.shutdown_timeout_seconds: must be a whole number.", run.Stdout);
        Assert.DoesNotContain(AdminFixtures.Canary, run.All);
    }
}
