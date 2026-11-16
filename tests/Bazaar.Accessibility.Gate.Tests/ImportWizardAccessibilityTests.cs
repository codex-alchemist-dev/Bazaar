using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Portability;
using Bazaar.Portability.Client;
using Bazaar.Portability.Client.Avalonia;

namespace Bazaar.Accessibility.Gate.Tests;

// Portability import wizard (doc 49 section 2). Every step view is in the logical tree of ImportWizardView, so one
// window per state scans the choose-source, detect, preview, dry-run, commit, done and failed controls. Each test also
// checks that the controls it scans were actually rendered, so an empty tree cannot pass the scan.
public class ImportWizardAccessibilityTests
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    [AvaloniaFact]
    public void Import_wizard_preview_step_has_named_controls()
    {
        var index = new StoredIndex(new ExistingObject("document", "Plan", "obj-1", "rev-9"));
        var window = ShowWizard(PreviewWithCollision(), index);

        Assert.Equal(2, window.GetVisualDescendants().OfType<ComboBox>().Count());
        Assert.Empty(AccessibleControlScan.Find(window));
    }

    [AvaloniaFact]
    public void Import_wizard_detect_step_has_named_controls()
    {
        var state = WizardState.Initial with
        {
            Step = WizardStep.Detect,
            Candidates =
            [
                new ImportCandidate("bazaar.import.markdown", 0.9, "Markdown headings"),
                new ImportCandidate("bazaar.import.plain", 0.4, "Plain paragraphs"),
            ],
        };
        var window = ShowWizard(state);

        Assert.Equal(2, window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible));
        Assert.Empty(AccessibleControlScan.Find(window));
    }

    private static Window ShowWizard(WizardState state, IExistingObjectIndex? index = null)
    {
        var viewModel = index is null
            ? new WizardViewModel(new ImportWizard(new ImportPipeline([])), new NoopCommitter(), NoKeys, () => Task.FromResult<ImportSource?>(null), state)
            : new WizardViewModel(new ImportWizard(new ImportPipeline([])), new NoopCommitter(), NoKeys, () => Task.FromResult<ImportSource?>(null), state, index);
        var window = new Window { DataContext = viewModel, Content = new ImportWizardView() };
        window.Show();
        // Items are only realised after a layout pass, so measure and arrange before scanning.
        window.Measure(Size.Infinity);
        window.Arrange(new Rect(window.DesiredSize));
        return window;
    }

    private static WizardState PreviewWithCollision() => WizardState.Initial with
    {
        Step = WizardStep.Preview,
        Preview = new PreviewSummary(
            NewCount: 2,
            DuplicateCount: 0,
            Targets: [new TargetPreview("document", 2, [new SourceRecord("a.md#1", "document", "Plan", "body", new Dictionary<string, string>())])],
            Collisions: [new PreviewCollision("document", "Plan", ["a.md#1", "b.md#1"])],
            Unmapped: []),
        Plan = PlanForPreview(),
        BasePlan = PlanForPreview(),
    };

    private static ImportPipelineResult PlanForPreview() =>
        new(ImportStatus.Ready, "bazaar.import.a", [], new ImportPreview(
            [
                new SourceRecord("a.md#1", "document", "Plan", "body", new Dictionary<string, string>()),
                new SourceRecord("b.md#1", "document", "Plan", "body", new Dictionary<string, string>()),
            ],
            []), "hash");

    private sealed class StoredIndex(params ExistingObject[] stored) : IExistingObjectIndex
    {
        public Task<IReadOnlyList<ExistingObject>> FindAsync(IReadOnlyList<(string Kind, string Title)> wanted, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingObject>>(stored.Where(s => wanted.Contains((s.Kind, s.Title))).ToList());
    }

    private sealed class NoopCommitter : IImportCommitter
    {
        public Task<ImportDryRunReport> DryRunAsync(ImportPipelineResult plan, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportDryRunReport(true, plan.MappingHash, 0, 0));

        public Task<ImportCommitReport> CommitAsync(ImportPipelineResult plan, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportCommitReport(true, 0));
    }
}
