using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Bazaar.Client.PullRequests.Tests;

public class PullRequestViewHeadlessTests
{
    private static Window Show(object viewModel, Control content)
    {
        var window = new Window { Width = 400, Height = 900, DataContext = viewModel, Content = content };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static T Find<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task Merge_button_is_disabled_when_merging_is_off_and_no_approve_control_exists()
    {
        var client = new FakePullRequestClient();
        client.Summaries.Enqueue(Rig.Summary());
        var vm = new PullRequestSummaryViewModel(
            Rig.Row(), client, new OwnerMergeGate(new FakeBiometric(), new FakeSigner(), Rig.Human, Rig.Settings(mergeFromFolded: false)),
            Rig.Settings(mergeFromFolded: false));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var window = Show(vm, new PullRequestSummaryView());

        Assert.False(Find<Button>(window, "MergeButton").IsEnabled);
        Assert.Equal("Reviewer approved: True (data only, not merge authority)", Find<TextBlock>(window, "ReviewerText").Text);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content is "Approve");
        Assert.False(Find<Button>(window, "DiffButton").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Diff_surface_draws_each_hunk_line_when_the_full_diff_is_on()
    {
        var client = new FakePullRequestClient();
        client.Summaries.Enqueue(Rig.Summary());
        var settings = Rig.Settings(foldedFullDiff: true);
        var vm = new PullRequestSummaryViewModel(
            Rig.Row(), client, new OwnerMergeGate(new FakeBiometric(), new FakeSigner(), Rig.Human, settings), settings);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        client.Hunks.Enqueue(new Bazaar.Contracts.GitHub.PrHunksReply(
            Bazaar.Contracts.GitHub.PrHunksStatus.Ok, Rig.HeadA, "src/widget.cs",
            [new Bazaar.Contracts.GitHub.PrHunk("@@ -1 +1 @@", 1, 1, 1, 1, [" a", "-b", "+c"])], null));
        await vm.OpenDiffAsync("src/widget.cs", TestContext.Current.CancellationToken);

        var window = Show(vm, new PullRequestSummaryView());

        Assert.Equal(4, Find<PullRequestDiffSurface>(window, "DiffSurface").LineCount);
        Assert.True(Find<Button>(window, "DiffButton").IsEnabled);
    }
}
