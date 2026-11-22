using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Setup.Wizard;

/// <summary>
/// The shared wizard backend (doc 45 section 1.1) used without any Bazaar.Admin type: a scripted prompter drives the engine, a
/// closed gate and a test renderer stand in for the CLI's pieces. This is what a server terminal UI composes.
/// </summary>
public sealed class SetupWizardBackendTests
{
    private const string _kekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin";
    private const string _anchor = "https://anchor.example.invalid/bazaar-audit";
    private const string _backup = "file:/mnt/backup/bazaar";

    private static readonly string[] _answers =
    [
        "Example Studio", "", "Owner", "", "dpapi", _kekRef, "", _anchor, _backup, "", "", "", "", "", "", "", "",
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

    [Fact]
    public async Task A_scripted_prompter_and_the_backend_give_the_flag_plan_hash_and_show_the_renderers_lines()
    {
        var backend = new SetupWizardBackend(_noEnvironment, new ClosedGate(), new TestRenderer(), RegistrySetupSettingCatalog.Known);
        var prompter = new ScriptedWizardPrompter(_answers);

        var result = await new SetupWizardEngine(prompter, backend).RunAsync(TestContext.Current.CancellationToken);

        var expected = backend.DryRun(_flags);
        Assert.Equal((int)SetupExitCode.Ok, result.ExitCode);
        Assert.Equal(expected.PlanHash, result.PlanHash);
        Assert.NotNull(expected.Report);
        Assert.Equal("dry-run", expected.Report.Command);
        Assert.Contains("test-render dry-run exit 0", prompter.Lines);
    }

    [Fact]
    public async Task A_closed_gate_holds_an_apply_and_never_reaches_the_applier()
    {
        var backend = new SetupWizardBackend(_noEnvironment, new ClosedGate(), new TestRenderer(), RegistrySetupSettingCatalog.Known);
        var hash = backend.DryRun(_flags).PlanHash!;

        var review = await backend.ApplyAsync(_flags, hash, SetupEntryPath.Wizard, TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Held, review.ExitCode);
        Assert.Equal(SetupOrchestrator.ApplyUnavailableOutcome, review.Report!.Outcome);
    }

    [Fact]
    public void The_change_view_hides_a_reference_value()
    {
        var shown = SetupChangeView.Show("custody.kekRef", "\"dpapi:secret-blob-name.bin\"");

        Assert.DoesNotContain("secret-blob-name", shown, StringComparison.Ordinal);
        Assert.Contains("dpapi", shown, StringComparison.Ordinal);
    }

    private sealed class ClosedGate : ICliApplyGate
    {
        public bool DurableAuditStoreAvailable => false;

        public void RegisterApplyServices(IServiceCollection services) => throw new InvalidOperationException("A closed gate registers nothing.");
    }

    private sealed class TestRenderer : ISetupReportRenderer
    {
        public IReadOnlyList<string> RenderLines(SetupReport report) => [$"test-render {report.Command} exit {report.ExitCode}"];
    }
}
