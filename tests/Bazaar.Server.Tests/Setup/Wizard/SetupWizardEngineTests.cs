using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup.Wizard;

/// <summary>
/// The UI-free wizard engine (doc 45 section 1.1) driven by a scripted prompter. The central claim is equivalence: the answers,
/// written as <c>--set</c> flags, give the same plan hash through the engine as through the overlay and loader directly.
/// </summary>
public sealed class SetupWizardEngineTests
{
    private const string _kekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin";
    private const string _anchor = "https://anchor.example.invalid/bazaar-audit";
    private const string _backup = "file:/mnt/backup/bazaar";
    private const string _canary = "canary-secret-0000";

    private static readonly string[] _mainAnswers =
    [
        "Example Studio", "", "Owner", "", "dpapi", _kekRef, "", _anchor, _backup, "", "", "", "", "",
        "Example Studio", "", "Owner", "", "dpapi", _kekRef, "", "", _anchor, _backup, "", "", "", "", "", "", "", "",
    ];

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

    private static readonly IReadOnlyDictionary<string, string?> _noEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);

    [Fact]
    public async Task The_engine_asks_the_model_questions_in_order()
    {
        var prompter = new ScriptedWizardPrompter(_mainAnswers);
        var backend = new FakeBackend();

        await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        var modelIds = SetupWizardSteps.Create().SelectMany(step => step.Questions).Select(question => question.Id);
        Assert.Equal([.. modelIds, SetupWizardSteps.PlanHashQuestionId], prompter.AskedIds);
    }

    [Fact]
    public async Task The_answers_give_the_same_plan_hash_as_the_flags()
    {
        var prompter = new ScriptedWizardPrompter(_mainAnswers);
        var backend = new FakeBackend();

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(_mainFlags.Order(StringComparer.Ordinal), result.SetArguments.Order(StringComparer.Ordinal));
        Assert.Equal(HashOf(_mainFlags), result.PlanHash);
        Assert.Equal(64, result.PlanHash!.Length);
        Assert.Empty(backend.Applies);
    }

    [Fact]
    public async Task A_typed_hash_prefix_applies_with_the_wizard_entry_path()
    {
        var hash = HashOf(_mainFlags);
        var prompter = new ScriptedWizardPrompter(_mainAnswers[..^1].Append(hash[..12]));
        var backend = new FakeBackend();

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Ok, result.ExitCode);
        var apply = Assert.Single(backend.Applies);
        Assert.Equal(SetupEntryPath.Wizard, apply.EntryPath);
        Assert.Equal(hash, apply.PlanHash);
    }

    [Theory]
    [InlineData("0123456789ab")]
    [InlineData("abc")]
    public async Task A_wrong_or_short_hash_prefix_applies_nothing(string typed)
    {
        var prompter = new ScriptedWizardPrompter(_mainAnswers[..^1].Append(typed));
        var backend = new FakeBackend();

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Empty(backend.Applies);
    }

    [Fact]
    public async Task Input_ending_early_stops_without_a_review()
    {
        var prompter = new ScriptedWizardPrompter(["Example Studio"]);
        var backend = new FakeBackend();

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Null(result.PlanHash);
        Assert.Equal(0, backend.DryRuns);
    }

    [Fact]
    public async Task A_held_plan_offers_no_apply()
    {
        var answers = _mainAnswers.ToArray();
        answers[10] = "lan";
        answers[11] = "lan";
        var prompter = new ScriptedWizardPrompter(answers);
        var backend = new FakeBackend();

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Held, result.ExitCode);
        Assert.DoesNotContain(SetupWizardSteps.PlanHashQuestionId, prompter.AskedIds);
    }

    [Fact]
    public async Task Progress_runs_through_every_step_then_the_review()
    {
        var prompter = new ScriptedWizardPrompter(_mainAnswers);

        await new SetupWizardEngine(prompter, new FakeBackend()).RunAsync(TestContext.Current.CancellationToken);

        var steps = SetupWizardSteps.Create();
        var questionPhase = prompter.Progress.Where(p => p.Phase == SetupProgressPhase.Questions).ToList();
        Assert.Equal(steps.Select(step => step.Id), questionPhase.Select(p => p.StepId));
        Assert.Equal(Enumerable.Range(1, steps.Count), questionPhase.Select(p => p.Current));
        Assert.All(prompter.Progress, p => Assert.Equal(steps.Count, p.Total));
        Assert.Equal(SetupProgressPhase.Review, prompter.Progress[^1].Phase);
    }

    [Fact]
    public async Task A_literal_where_a_reference_belongs_is_refused_and_not_echoed()
    {
        var answers = _mainAnswers.ToList();
        answers.Insert(5, _canary);
        var prompter = new ScriptedWizardPrompter(answers);
        var backend = new FakeBackend();

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Ok, result.ExitCode);
        Assert.DoesNotContain(prompter.Lines, line => line.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(prompter.Prompts, prompt => prompt.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(result.SetArguments, flag => flag.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(backend.AllText(), text => text.Contains(_canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_secret_goes_to_the_writer_and_only_its_reference_reaches_the_plan()
    {
        const string reference = "secret://bazaar/install/example";
        var steps = new List<WizardStep>
        {
            new("secrets", "Secrets",
            [
                new WizardQuestion
                {
                    Id = "example.secret",
                    Prompt = "Example key",
                    Kind = WizardQuestionKind.Secret,
                    PlanPath = "custody.kekRef",
                    SecretReference = reference,
                },
            ]),
        };
        var prompter = new ScriptedWizardPrompter([_canary, ""]);
        var backend = new FakeBackend();
        var writer = new RecordingSecretWriter(available: true);

        var result = await new SetupWizardEngine(prompter, backend, writer, steps).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((reference, _canary), Assert.Single(writer.Writes));
        Assert.Contains("custody.kekRef=" + reference, result.SetArguments);
        Assert.DoesNotContain(result.SetArguments, flag => flag.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(prompter.Lines, line => line.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(backend.AllText(), text => text.Contains(_canary, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_secret_that_cannot_be_stored_stops_the_wizard_and_shows_no_value(bool available, bool writerThrows)
    {
        var steps = new List<WizardStep>
        {
            new("secrets", "Secrets",
            [
                new WizardQuestion
                {
                    Id = "example.secret",
                    Prompt = "Example key",
                    Kind = WizardQuestionKind.Secret,
                    PlanPath = "custody.kekRef",
                    SecretReference = "secret://bazaar/install/example",
                },
            ]),
        };
        var prompter = new ScriptedWizardPrompter([_canary]);
        var backend = new FakeBackend();
        var writer = new RecordingSecretWriter(available) { ThrowWithValue = writerThrows };

        var result = await new SetupWizardEngine(prompter, backend, writer, steps).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Equal(0, backend.DryRuns);
        Assert.DoesNotContain(prompter.Lines, line => line.Contains(_canary, StringComparison.Ordinal));
        Assert.DoesNotContain(result.SetArguments, flag => flag.Contains(_canary, StringComparison.Ordinal));
    }

    [Fact]
    public void The_secret_input_never_shows_its_value_and_wipes_on_dispose()
    {
        var input = new SetupSecretInput(_canary);

        Assert.DoesNotContain(_canary, input.ToString(), StringComparison.Ordinal);
        Assert.Equal(_canary, input.Reveal());
        input.Dispose();
        Assert.DoesNotContain(_canary, input.Reveal(), StringComparison.Ordinal);
    }

    private static string HashOf(IEnumerable<string> flags)
    {
        var merged = SetupDoorOverlay.Merge(null, flags.ToList(), _noEnvironment, RegistrySetupSettingCatalog.Known);
        return SetupPlanLoader.Load(merged.MergedJson!, RegistrySetupSettingCatalog.Known).PlanHash ?? string.Empty;
    }

    /// <summary>The overlay, loader and hash of the real doors, with the apply recorded instead of run.</summary>
    private sealed class FakeBackend : ISetupWizardBackend
    {
        private readonly List<string> _text = [];

        public int DryRuns { get; private set; }

        public List<(string PlanHash, SetupEntryPath EntryPath)> Applies { get; } = [];

        public List<string> AllText() => _text;

        public SetupWizardReview DryRun(IReadOnlyList<string> setArguments)
        {
            DryRuns++;
            _text.AddRange(setArguments.Where(flag => !flag.StartsWith("custody.kekRef=", StringComparison.Ordinal)));
            var merged = SetupDoorOverlay.Merge(null, setArguments, _noEnvironment, RegistrySetupSettingCatalog.Known);
            if (merged.MergedJson is null)
            {
                return new SetupWizardReview((int)SetupExitCode.InvalidInput, null, ["Error"]);
            }

            var load = SetupPlanLoader.Load(merged.MergedJson, RegistrySetupSettingCatalog.Known);
            var lines = new List<string> { $"Plan hash: {load.PlanHash}" };
            lines.AddRange(load.Issues.Select(issue => $"{issue.Severity} at {issue.Path}"));
            return new SetupWizardReview((int)load.ExitCode, load.Plan is null ? null : load.PlanHash, lines);
        }

        public Task<SetupWizardReview> ApplyAsync(IReadOnlyList<string> setArguments, string planHash, SetupEntryPath entryPath, CancellationToken cancellationToken)
        {
            Applies.Add((planHash, entryPath));
            return Task.FromResult(new SetupWizardReview((int)SetupExitCode.Ok, planHash, ["applied"]));
        }
    }

    private sealed class RecordingSecretWriter(bool available) : ISetupSecretWriter
    {
        public bool StoreAvailable => available;

        public bool ThrowWithValue { get; init; }

        public List<(string Reference, string Value)> Writes { get; } = [];

        public Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            if (ThrowWithValue)
            {
                throw new InvalidOperationException("failed with " + value);
            }

            Writes.Add((reference, value));
            return Task.CompletedTask;
        }
    }
}
