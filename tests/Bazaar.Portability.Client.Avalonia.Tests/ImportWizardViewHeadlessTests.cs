using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Portability;
using Bazaar.Portability.Client;

namespace Bazaar.Portability.Client.Avalonia.Tests;

public class ImportWizardViewHeadlessTests
{
    private static readonly IReadOnlySet<string> _noKeys = new HashSet<string>(StringComparer.Ordinal);

    private static WizardViewModel ViewModelAt(WizardState state) =>
        new(
            new ImportWizard(new ImportPipeline([])),
            new NoopCommitter(),
            _noKeys,
            () => Task.FromResult<ImportSource?>(null),
            state);

    private static WizardState PreviewWithCollision() => WizardState.Initial with
    {
        Step = WizardStep.Preview,
        Preview = new PreviewSummary(
            NewCount: 2,
            DuplicateCount: 0,
            Targets: [new TargetPreview("document", 2, [new SourceRecord("a.md#1", "document", "Plan", "body", new Dictionary<string, string>())])],
            Collisions: [new PreviewCollision("document", "Plan", ["a.md#1", "b.md#1"])],
            Unmapped: []),
    };

    [AvaloniaFact]
    public void Preview_state_shows_the_collision_and_its_per_item_choice()
    {
        var viewModel = ViewModelAt(PreviewWithCollision());
        var window = new Window { DataContext = viewModel, Content = new ImportWizardView() };
        window.Show();
        // Item containers are created during layout; force one pass so the collision rows exist.
        Dispatcher.UIThread.RunJobs();
        window.Measure(Size.Infinity);
        window.Arrange(new Rect(window.DesiredSize));
        Dispatcher.UIThread.RunJobs();

        var preview = window.GetVisualDescendants().OfType<PreviewStepView>().Single();
        var choice = window.GetVisualDescendants().OfType<ComboBox>().Single();

        Assert.True(preview.IsVisible);
        Assert.Equal("a.md#1", choice.SelectedItem);
        Assert.Equal(2, choice.ItemCount);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Plan");
    }

    [AvaloniaFact]
    public void Only_the_view_for_the_current_step_is_visible()
    {
        var viewModel = ViewModelAt(PreviewWithCollision());
        var window = new Window { DataContext = viewModel, Content = new ImportWizardView() };
        window.Show();

        Assert.True(window.GetVisualDescendants().OfType<PreviewStepView>().Single().IsVisible);
        Assert.False(window.GetVisualDescendants().OfType<DetectStepView>().Single().IsVisible);
        Assert.False(window.GetVisualDescendants().OfType<DoneStepView>().Single().IsVisible);
    }

    private sealed class NoopCommitter : IImportCommitter
    {
        public Task<ImportDryRunReport> DryRunAsync(ImportPipelineResult plan, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportDryRunReport(true, plan.MappingHash, 0, 0));

        public Task<ImportCommitReport> CommitAsync(ImportPipelineResult plan, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportCommitReport(true, 0));
    }
}
