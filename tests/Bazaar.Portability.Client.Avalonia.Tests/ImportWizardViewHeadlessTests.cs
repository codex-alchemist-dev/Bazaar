using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
        Plan = PlanForPreview(),
        BasePlan = PlanForPreview(),
    };

    [AvaloniaFact]
    public void Preview_state_shows_the_collision_and_its_per_item_choice()
    {
        var viewModel = ViewModelAt(PreviewWithCollision());
        var window = new Window { Width = 800, Height = 900, DataContext = viewModel, Content = new ImportWizardView() };
        window.Show();
        // Item containers are realized on layout; force a render tick and let queued jobs run.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var preview = window.GetVisualDescendants().OfType<PreviewStepView>().Single();
        var choice = window.GetVisualDescendants().OfType<ComboBox>().Single();

        Assert.True(preview.IsVisible);
        Assert.Null(choice.SelectedItem);
        Assert.Equal("Keep both (flagged)", choice.PlaceholderText);
        Assert.Equal(2, choice.ItemCount);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Plan");
    }

    [AvaloniaFact]
    public void Choosing_a_collision_record_updates_the_wizard_state()
    {
        var viewModel = ViewModelAt(PreviewWithCollision());

        viewModel.Collisions[0].SelectedKey = "b.md#1";

        var decision = Assert.Single(viewModel.State.CollisionChoices);
        Assert.Equal("b.md#1", decision.KeepSourceKey);
        Assert.Equal("b.md#1", viewModel.Collisions[0].SelectedKey);
    }

    [AvaloniaFact]
    public void Retry_command_is_enabled_only_in_the_failed_step_and_returns_to_the_preview()
    {
        var failed = PreviewWithCollision() with { Step = WizardStep.Failed, Message = "disk full" };
        var viewModel = ViewModelAt(failed);

        Assert.True(viewModel.RetryCommand.CanExecute(null));
        viewModel.RetryCommand.Execute(null);

        Assert.Equal(WizardStep.Preview, viewModel.State.Step);
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    private static ImportPipelineResult PlanForPreview() =>
        new(ImportStatus.Ready, "bazaar.import.a", [], new ImportPreview([], []), "hash");

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
