using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Panes;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

public class GitSolarPaneViewTests
{
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
