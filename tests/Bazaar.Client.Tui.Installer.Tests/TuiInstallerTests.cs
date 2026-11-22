using Bazaar.Client.Tui.Tests;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Client.Tui.Installer.Tests;

/// <summary>
/// The installer head over the real wizard engine, real steps and the real shared backend (closed gate): the TUI prompter gives the
/// same plan hash as the flag door, the secret never reaches the screen, and the plain fallback is used outside a TTY.
/// </summary>
public sealed class TuiInstallerTests
{
    private const string _kekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin";
    private const string _anchor = "https://anchor.example.invalid/bazaar-audit";
    private const string _backup = "file:/mnt/backup/bazaar";
    private const string _canary = "CANARY-s3cret-9f2c";

    private static readonly string[] _answers =
    [
        "Example Studio", "", "Owner", "", "dpapi", _kekRef, "", "", _anchor, _backup, "", "", "", "", "", "", "", "", "", "", "",
    ];

    private static readonly string[] _flags =
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

    private static Func<ISetupReportRenderer, ISetupWizardBackend> Backend() =>
        TuiInstaller.SharedBackend(_noEnvironment, new ClosedGate(), RegistrySetupSettingCatalog.Known);

    private static TerminalEnvironment Env(bool outputRedirected = false, string? noColor = null, params string[] arguments) =>
        new(false, outputRedirected, noColor, "xterm", arguments);

    [Fact]
    public async Task The_tui_prompter_over_the_real_engine_gives_the_flag_doors_plan_hash()
    {
        var terminal = new ScriptedTerminal().Lines(_answers);
        var options = new TuiInstallerOptions(Env(), terminal, Backend(), () => throw new InvalidOperationException("The plain path must not run."));

        var run = await TuiInstaller.RunAsync(options, TestContext.Current.CancellationToken);

        var expected = Backend()(new TuiSetupReportRenderer()).DryRun(_flags);
        Assert.Equal(TuiMode.FullScreen, run.Mode.Mode);
        Assert.Equal((int)SetupExitCode.Ok, run.Result.ExitCode);
        Assert.Equal(expected.PlanHash, run.Result.PlanHash);
        Assert.Equal(_flags, run.Result.SetArguments);
        Assert.Contains("Plan hash: " + expected.PlanHash, terminal.Output, StringComparison.Ordinal);
        Assert.Contains("Writes: none.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installer_asks_every_step_with_a_status_line_and_no_quick_path()
    {
        var terminal = new ScriptedTerminal().Lines(_answers);
        var options = new TuiInstallerOptions(Env(), terminal, Backend(), () => throw new InvalidOperationException());

        await TuiInstaller.RunAsync(options, TestContext.Current.CancellationToken);

        var steps = SetupWizardSteps.Create();
        Assert.Contains($"Step 1 of {steps.Count}: {steps[0].Title}", terminal.Output, StringComparison.Ordinal);
        Assert.Contains($"Step {steps.Count} of {steps.Count}: {steps[^1].Title}", terminal.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null, "")]
    [InlineData(false, "1", "")]
    [InlineData(false, null, "--plain")]
    [InlineData(false, null, "--screen-reader")]
    public async Task Outside_a_tty_or_with_a_plain_signal_the_plain_prompter_runs_and_the_tui_draws_nothing(bool outputRedirected, string? noColor, string argument)
    {
        var terminal = new ScriptedTerminal();
        var plain = new PlainRecorder();
        var arguments = argument.Length == 0 ? [] : new[] { argument };
        var options = new TuiInstallerOptions(Env(outputRedirected, noColor, arguments), terminal, Backend(), () => plain);

        var run = await TuiInstaller.RunAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(TuiMode.Plain, run.Mode.Mode);
        Assert.True(plain.Asked > 0);
        Assert.Equal(string.Empty, terminal.Output);
    }

    [Fact]
    public async Task Ended_input_stops_before_anything_is_applied()
    {
        var terminal = new ScriptedTerminal().Lines("Example Studio");
        var options = new TuiInstallerOptions(Env(), terminal, Backend(), () => throw new InvalidOperationException());

        var run = await TuiInstaller.RunAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.Result.ExitCode);
        Assert.Null(run.Result.PlanHash);
        Assert.Contains("Nothing was changed.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_question_is_stored_through_the_writer_and_never_reaches_the_screen_or_the_plan()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Typed(_canary);
        var writer = new RecordingSecretWriter();
        var question = new WizardQuestion
        {
            Id = "provider.key",
            Prompt = "Provider key",
            Kind = WizardQuestionKind.Secret,
            PlanPath = "custody.kekRef",
            SecretReference = "secret://bazaar/providers/test/key",
        };
        var prompter = new TuiSetupPrompter(new TerminalPromptWidgets(terminal), terminal, new TuiStyle(useColor: false));
        var backend = new CapturingBackend();
        var engine = new SetupWizardEngine(prompter, backend, writer, [new WizardStep("secret", "Secret", [question])]);

        await engine.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((_canary, "secret://bazaar/providers/test/key"), (writer.Value, writer.Reference));
        Assert.DoesNotContain(_canary, terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(backend.Sets, set => set.Contains(_canary, StringComparison.Ordinal));
        Assert.Contains("custody.kekRef=secret://bazaar/providers/test/key", backend.Sets);
    }

    [Fact]
    public void The_report_renderer_lists_issues_changes_and_the_hash_and_never_a_value_it_was_not_given()
    {
        var report = new SetupReport
        {
            Command = "dry-run",
            ExitCode = 0,
            Result = "valid",
            PlanHash = "abc123",
            Changes = [new SetupReportChange { Path = "install.companyName", Kind = "create", Planned = "Example" }],
            Issues = [new SetupReportIssue { Severity = "hold", Path = "custody.kekRef", Message = "needs approval" }],
        };

        var lines = new TuiSetupReportRenderer().RenderLines(report);

        Assert.Contains("Result: valid (exit 0)", lines);
        Assert.Contains("Plan hash: abc123", lines);
        Assert.Contains("Hold at custody.kekRef: needs approval", lines);
        Assert.Contains("  create install.companyName = Example", lines);
        Assert.Contains("Writes: none.", lines);
    }

    private sealed class ClosedGate : ICliApplyGate
    {
        public bool DurableAuditStoreAvailable => false;

        public void RegisterApplyServices(IServiceCollection services) => throw new InvalidOperationException("A closed gate registers nothing.");
    }

    /// <summary>A plain prompter stand-in that counts questions and ends the input, so the engine stops at once.</summary>
    private sealed class PlainRecorder : ISetupPrompter
    {
        public int Asked { get; private set; }

        public void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info)
        {
        }

        public void ShowProgress(SetupProgress progress)
        {
        }

        public string? AskText(WizardQuestion question) => Ended();

        public string? ChooseOne(WizardQuestion question) => Ended();

        public IReadOnlyList<string>? ChooseMany(WizardQuestion question) => null;

        public bool? Confirm(WizardQuestion question) => null;

        public SetupSecretInput? AskSecret(WizardQuestion question) => null;

        private string? Ended()
        {
            Asked++;
            return null;
        }
    }

    private sealed class RecordingSecretWriter : ISetupSecretWriter
    {
        public string? Value { get; private set; }

        public string? Reference { get; private set; }

        public bool StoreAvailable => true;

        public Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            Reference = reference;
            Value = value;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingBackend : ISetupWizardBackend
    {
        public IReadOnlyList<string> Sets { get; private set; } = [];

        public SetupWizardReview DryRun(IReadOnlyList<string> setArguments)
        {
            Sets = setArguments.ToList();
            return new SetupWizardReview((int)SetupExitCode.InvalidInput, null, ["captured"]);
        }

        public Task<SetupWizardReview> ApplyAsync(IReadOnlyList<string> setArguments, string planHash, SetupEntryPath entryPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The review fails, so nothing is applied.");
    }
}
