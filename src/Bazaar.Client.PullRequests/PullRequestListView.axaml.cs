using Avalonia.Controls;

namespace Bazaar.Client.PullRequests;

/// <summary>The PR list: refresh and paging. Opening a PR is the host's job; this view never merges.</summary>
public sealed partial class PullRequestListView : UserControl
{
    public PullRequestListView()
    {
        InitializeComponent();
        RefreshButton.Click += (_, _) => _ = (DataContext as PullRequestListViewModel)?.RefreshAsync();
        MoreButton.Click += (_, _) => _ = (DataContext as PullRequestListViewModel)?.LoadMoreAsync();
    }
}
