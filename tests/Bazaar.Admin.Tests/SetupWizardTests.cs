using Bazaar.Admin;
using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Admin.Tests;

/// <summary>
/// The interactive wizard (doc 45 section 1.1). The main claim is equivalence: the answers, written through the same overlay as
/// <c>--set</c> flags, give the same plan hash as the flags, the file and the environment. The rest checks the question order, the
/// re-asking on bad input, the warnings and holds, and that apply runs only after the typed hash prefix.
/// </summary>
public sealed class SetupWizardTests
{
    private const string _kekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin";
    private const string _anchor = "https://anchor.example.invalid/bazaar-audit";
    private const string _backup = "file:/mnt/backup/bazaar";

    /// <summary>The answers for the default-heavy path: every question takes its default unless a value is given here.</summary>
    private static readonly string[] _mainAnswers =
    [
        "Example Studio",   // company name
        "",                 // company slug (default: example-studio)
        "Owner",            // owner display name
        "",                 // release channel (default: beta)
        "dpapi",            // key-encryption key provider
        _kekRef,            // key reference
        "",                 // company source (default: blank)
        _anchor,            // audit anchor
        _backup,            // backup target
        "",                 // second factor (default: yes)
        "",                 // status bind (default: loopback)
        "",                 // status port (default: 8443)
        "",                 // modules (default: none)
        "",                 // apply prompt: stop without applying
    ];

    /// <summary>The same answers written as the flags a scripted run would give. This is the list the equivalence test checks.</summary>
    private static readonly string[] _mainFlags =
    [
        "schemaVersion=1",
        "install.companyName=Example Studio",
        "install.companySlug=example-studio",
        "owner.displayName=Owner",
        "install.releaseChannel=beta",
        "custody.kekProvider=dpapi",
        "custody.kekRef=" + _kekRef,
        "company.source=blank",
        "custody.auditAnchorTarget=" + _anchor,
        "backups.target=" + _backup,
        "network.statusBind=loopback",
        "network.statusPort=8443",
        "modules=[\"Bazaar.GC\"]",
    ];

    private static async Task<CliRun> RunWizard(ScriptedSetupPrompter prompter, IReadOnlyDictionary<string, string?>? environment = null, ICliApplyGate? gate = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AdminCommandLine.RunAsync(
            ["setup"],
            stdout,
            stderr,
            applyGate: gate,
            environment: new DictionarySetupEnvironment(environment ?? new Dictionary<string, string?>(StringComparer.Ordinal)),
            prompter: prompter,
            cancellationToken: TestContext.Current.CancellationToken);
        return new CliRun(code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs a flag-form command with the same environment the wizard had, so only the entry door differs.</summary>
    private static async Task<CliRun> RunFlags(IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AdminCommandLine.RunAsync(
            args,
            stdout,
            stderr,
            environment: new DictionarySetupEnvironment(environment),
            cancellationToken: TestContext.Current.CancellationToken);
        return new CliRun(code, stdout.ToString(), stderr.ToString());
    }

    private static string[] SetArgs(IEnumerable<string> flags) => flags.SelectMany(flag => new[] { "--set", flag }).ToArray();

    private static string HashOf(string output)
    {
        var line = output.Split('\n').Single(l => l.StartsWith("Plan hash: ", StringComparison.Ordinal));
        return line["Plan hash: ".Length..].Trim();
    }

    private static string[] Args(params string[] tail) => ["setup", "--dry-run", .. tail];

    [Fact]
    public async Task Wizard_answers_give_the_same_plan_hash_as_the_flags_the_file_and_the_environment()
    {
        var prompter = new ScriptedSetupPrompter(_mainAnswers);
        var wizard = await RunWizard(prompter);
        var wizardHash = prompter.PlanHash;

        Assert.Equal((int)SetupExitCode.Ok, wizard.ExitCode);
        Assert.NotNull(wizardHash);
        Assert.Equal(64, wizardHash!.Length);

        // Flags door: the same answers as --set flags.
        var flags = await RunFlags(new Dictionary<string, string?>(StringComparer.Ordinal), Args(SetArgs(_mainFlags)));
        Assert.Equal((int)SetupExitCode.Ok, flags.ExitCode);
        Assert.Equal(wizardHash, HashOf(flags.Stdout));

        // File door: the merged document the overlay builds from the same flags, written to a file.
        var merged = SetupDoorOverlay.Merge(null, _mainFlags, new Dictionary<string, string?>(StringComparer.Ordinal), RegistrySetupSettingCatalog.Known);
        using var folder = new TempSetupFolder();
        var config = folder.Write(merged.MergedJson!);
        var file = await RunFlags(new Dictionary<string, string?>(StringComparer.Ordinal), "setup", "--dry-run", "--config", config);
        Assert.Equal(wizardHash, HashOf(file.Stdout));

        // Environment door: the same answers as BAZAAR__SETUP__ variables.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var flag in _mainFlags)
        {
            var equals = flag.IndexOf('=');
            var path = flag[..equals];
            environment["BAZAAR__SETUP__" + string.Join("__", path.Split('.')).ToUpperInvariant()] = flag[(equals + 1)..];
        }

        var env = await RunFlags(environment, "setup", "--dry-run");
        Assert.Equal(wizardHash, HashOf(env.Stdout));
    }

    [Fact]
    public async Task Stopping_at_the_apply_prompt_applies_nothing()
    {
        var prompter = new ScriptedSetupPrompter(_mainAnswers);
        await RunWizard(prompter);

        Assert.DoesNotContain(prompter.Lines, line => line.Contains(AdminFixtures.ApplyUnavailableLine, StringComparison.Ordinal));
        Assert.Contains(prompter.Lines, line => line.StartsWith("Stopped without applying", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_typed_hash_prefix_reaches_the_apply_gate_and_the_closed_gate_refuses()
    {
        var first = new ScriptedSetupPrompter(_mainAnswers);
        await RunWizard(first);
        var hash = first.PlanHash!;

        var answers = _mainAnswers[..^1].Append(hash[..12]).ToArray();
        var prompter = new ScriptedSetupPrompter(answers);
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Contains(prompter.Lines, line => line.Contains(AdminFixtures.ApplyUnavailableLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hash_prefix_that_does_not_match_applies_nothing()
    {
        var prompter = new ScriptedSetupPrompter(_mainAnswers[..^1].Append("0123456789ab").ToArray());
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.DoesNotContain(prompter.Lines, line => line.Contains(AdminFixtures.ApplyUnavailableLine, StringComparison.Ordinal));
        Assert.Contains(prompter.Lines, line => line.Contains("does not match the start of the plan hash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Input_ending_before_the_review_stops_with_invalid_input_and_writes_nothing()
    {
        var prompter = new ScriptedSetupPrompter(["Example Studio"]);
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Contains(prompter.Lines, line => line.Contains("stopped before the review", StringComparison.Ordinal));
        Assert.Null(prompter.PlanHash);
    }

    [Fact]
    public async Task A_bad_port_is_asked_again_until_it_is_a_port()
    {
        var answers = _mainAnswers.ToList();
        var portIndex = 11;
        answers.InsertRange(portIndex, ["abc", "70000"]);
        var prompter = new ScriptedSetupPrompter(answers);
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Equal(3, prompter.Questions.Count(question => question.StartsWith("Status listener port", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Declining_the_second_factor_warns_and_matches_the_flag_form()
    {
        var answers = _mainAnswers.ToArray();
        answers[9] = "no";
        var prompter = new ScriptedSetupPrompter(answers);
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains(prompter.Lines, line => line.StartsWith("Warning: setup will finish without a second factor", StringComparison.Ordinal));
        var expected = await RunFlags(new Dictionary<string, string?>(StringComparer.Ordinal), Args(SetArgs(_mainFlags.Append("owner.requireSecondFactor=false"))));
        Assert.Equal(HashOf(expected.Stdout), prompter.PlanHash);
    }

    [Fact]
    public async Task Finishing_without_a_backup_target_needs_the_acknowledgement_and_matches_the_flags()
    {
        // No backup target, then the acknowledgement. The flags carry the same two answers.
        var answers = _mainAnswers.ToArray();
        answers[8] = "";
        var prompter = new ScriptedSetupPrompter(answers.Take(8).Append("").Append("yes").Concat(answers.Skip(9)).ToArray());
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        var flags = _mainFlags.Where(flag => !flag.StartsWith("backups.target=", StringComparison.Ordinal)).Append("backups.acknowledgeNoTarget=true");
        var expected = await RunFlags(new Dictionary<string, string?>(StringComparer.Ordinal), Args(SetArgs(flags)));
        Assert.Equal(HashOf(expected.Stdout), prompter.PlanHash);
    }

    [Fact]
    public async Task A_module_choice_goes_through_the_same_list_as_the_flags()
    {
        // Numbers 1 and 3 are Bazaar.Constellation and Bazaar.KODO; Bazaar.GC is always first.
        var answers = _mainAnswers.ToArray();
        answers[12] = "1, 3";
        var prompter = new ScriptedSetupPrompter(answers);
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        var flags = _mainFlags.Where(flag => !flag.StartsWith("modules=", StringComparison.Ordinal))
            .Append("modules=[\"Bazaar.GC\",\"Bazaar.Constellation\",\"Bazaar.KODO\"]");
        var expected = await RunFlags(new Dictionary<string, string?>(StringComparer.Ordinal), Args(SetArgs(flags)));
        Assert.Equal(HashOf(expected.Stdout), prompter.PlanHash);
    }

    [Fact]
    public async Task A_literal_typed_where_a_reference_belongs_is_refused_and_not_echoed()
    {
        const string canary = "canary-secret-0000";
        var answers = _mainAnswers.ToArray();
        answers[5] = canary;
        var prompter = new ScriptedSetupPrompter(answers.Take(5).Append(canary).Append(_kekRef).Concat(answers.Skip(6)).ToArray());
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.DoesNotContain(canary, prompter.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(prompter.Questions, question => question.Contains(canary, StringComparison.Ordinal));
        Assert.DoesNotContain(canary, run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_loopback_status_bind_is_held_and_offers_no_apply()
    {
        // lan is a registry value that needs owner approval (Tier 2), so the plan is held, not refused.
        var answers = _mainAnswers.ToArray();
        answers[10] = "lan";
        var prompter = new ScriptedSetupPrompter(answers);
        var run = await RunWizard(prompter);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.DoesNotContain(prompter.Questions, question => question.StartsWith("Apply this plan", StringComparison.Ordinal));
        Assert.Contains(prompter.Lines, line => line.Contains("held item needs approval", StringComparison.Ordinal));
    }
}
