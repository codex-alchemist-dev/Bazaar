using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

public class GitSolarPaneViewTests
{
    [AvaloniaFact]
    public void A_snapshot_and_frame_draw_the_canvas_and_a_click_on_a_commit_selects_it()
    {
        var source = new FakeGitSolarSource();
        using var pane = new GitSolarPaneViewModel(GitSolarFixtures.Descriptor(), source);
        var view = new GitSolarPaneView(pane);
        var window = new Window { Content = view, Width = 800, Height = 700 };
        window.Show();

        source.Push(new GitSolarUpdate.Snapshot(GitSolarFixtures.Snapshot()));
        source.Push(new GitSolarUpdate.Frame(Frame()));
        var canvas = view.GetVisualDescendants().OfType<GitSolarCanvas>().Single();
        Eventually.UntilUiIdle(() => canvas.NodeCount == 3, "canvas drawn");

        // The newest commit is selected by default, and the canvas highlights it through the model.
        Assert.Equal(GitSolarFixtures.MergeSha, pane.SelectedSha);

        Point? feature = canvas.NodeScreenPosition(GitSolarFixtures.FeatureSha);
        Assert.NotNull(feature);
        canvas.ClickAt(feature.Value);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GitSolarFixtures.FeatureSha, pane.SelectedSha);
        Assert.Equal(1, pane.SelectedIndex);

        window.Close();
    }

    /// <summary>One layout frame for the fixture snapshot: node 0 at the origin, node 1 to the right, node 2 below.</summary>
    private static LayoutFrame Frame() => new(
        GraphVersion: 1,
        LayoutSeq: 1,
        FocusId: GitSolarFixtures.MergeSha,
        Xyz: new float[] { 0f, 0f, 0f, 100f, 0f, 0f, 0f, 100f, 0f });

    [AvaloniaFact]
    public void The_view_renders_the_rows_and_Down_then_Enter_requests_the_next_commit()
    {
        var source = new FakeGitSolarSource();
        using var pane = new GitSolarPaneViewModel(GitSolarFixtures.Descriptor(), source);
        var opened = new List<GitSolarOpenCommit>();
        pane.OpenCommitRequested += opened.Add;

        var view = new GitSolarPaneView(pane);
        var window = new Window { Content = view };
        window.Show();

        source.Push(new GitSolarUpdate.Snapshot(GitSolarFixtures.Snapshot()));
        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Eventually.UntilUiIdle(() => list.ItemCount == 3, "rows rendered");

        Assert.Equal(0, list.SelectedIndex);
        var firstRow = Assert.IsType<ListBoxItem>(list.Items[0]);
        Assert.Contains("Merge the solar branch", AutomationProperties.GetName(firstRow), StringComparison.Ordinal);
        Assert.Single(view.GetVisualDescendants().OfType<ContentControl>(), control => control.Name == "PART_Canvas");

        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = list });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, pane.SelectedIndex);
        Assert.Equal(1, list.SelectedIndex);

        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = list });
        Dispatcher.UIThread.RunJobs();

        var request = Assert.Single(opened);
        Assert.Equal(GitSolarFixtures.FeatureSha, request.Sha);
        Assert.Equal("agent/solar", request.Branch);

        window.Close();
    }
}
