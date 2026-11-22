using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

public class GitSolarPaneViewModelTests
{
    private static GitSolarPaneViewModel Connected(FakeGitSolarSource source) =>
        new(GitSolarFixtures.Descriptor(), source);

    private static GitSolarPaneViewModel WithSnapshot(FakeGitSolarSource source, GitSolarSnapshot? snapshot = null)
    {
        source.Push(new GitSolarUpdate.Snapshot(snapshot ?? GitSolarFixtures.Snapshot()));
        var pane = Connected(source);
        return pane;
    }

    private static GitSolarPaneViewModel Loaded(out FakeGitSolarSource source)
    {
        source = new FakeGitSolarSource();
        var pane = Connected(source);
        source.Push(new GitSolarUpdate.Snapshot(GitSolarFixtures.Snapshot()));
        Eventually.Until(() => pane.Items.Count == 3, "snapshot rows");
        return pane;
    }

    [Fact]
    public void Snapshot_lists_commits_newest_first_with_branch_and_comet_marker()
    {
        using var pane = Loaded(out _);

        Assert.Equal("Git · Solar System", pane.Title);
        Assert.Equal(PaneKinds.GitSolarSystem, pane.Kind);
        Assert.Equal("owner/repo", pane.Repository);
        Assert.Equal("Live", pane.StatusText);

        Assert.Equal(
            [GitSolarFixtures.MergeSha, GitSolarFixtures.FeatureSha, GitSolarFixtures.BaseSha],
            pane.Items.Select(item => item.Sha));

        var merge = pane.Items[0];
        Assert.Equal("a1b2c3d", merge.ShortSha);
        Assert.Equal("Merge the solar branch", merge.Subject);
        Assert.Equal("main", merge.Branch);
        Assert.True(merge.IsComet);
        Assert.Contains("Merge the solar branch", merge.Text, StringComparison.Ordinal);

        Assert.Equal("agent/solar", pane.Items[1].Branch);
        Assert.False(pane.Items[2].IsComet);
    }

    [Fact]
    public void Newest_commit_is_selected_after_the_snapshot()
    {
        using var pane = Loaded(out _);

        Assert.Equal(0, pane.SelectedIndex);
        Assert.Equal(GitSolarFixtures.MergeSha, pane.SelectedSha);
    }

    [Fact]
    public void A_frame_is_refused_and_a_disconnect_marks_the_last_state_stale()
    {
        using var pane = Loaded(out var source);

        source.Push(new GitSolarUpdate.Frame(new LayoutFrame(1, 1, string.Empty, ReadOnlyMemory<float>.Empty)));
        source.Push(new GitSolarUpdate.Disconnected());
        Eventually.Until(() => pane.IsStale, "stale after disconnect");

        Assert.Equal("Stale", pane.StatusText);
        Assert.Equal(3, pane.Items.Count);
        Assert.Equal(0, pane.SelectedIndex);
    }

    [Fact]
    public void A_new_snapshot_after_a_disconnect_clears_the_stale_state()
    {
        using var pane = Loaded(out var source);
        source.Push(new GitSolarUpdate.Disconnected());
        Eventually.Until(() => pane.IsStale, "stale");

        source.Push(new GitSolarUpdate.Snapshot(GitSolarFixtures.Snapshot(graphVersion: 2)));
        Eventually.Until(() => !pane.IsStale, "stale cleared");

        Assert.Equal("Live", pane.StatusText);
    }

    [Fact]
    public void A_feed_that_ends_without_cancellation_marks_the_pane_stale()
    {
        using var pane = Loaded(out var source);

        source.Complete();
        Eventually.Until(() => pane.IsStale, "stale when the feed ends");

        Assert.Equal("Stale", pane.StatusText);
    }

    [Fact]
    public void A_snapshot_with_an_older_graph_version_is_dropped()
    {
        using var pane = Loaded(out var source);

        source.Push(new GitSolarUpdate.Snapshot(GitSolarFixtures.Snapshot(graphVersion: 0, withFeatureOnly: true)));
        source.Push(new GitSolarUpdate.Disconnected());
        Eventually.Until(() => pane.IsStale, "stale marker processed");

        Assert.Equal(3, pane.Items.Count);
    }

    [Fact]
    public void FocusCommit_selects_the_sha()
    {
        using var pane = Loaded(out _);

        Assert.True(pane.Handle(GitSolarCommands.FocusCommit, GitSolarFixtures.BaseSha));

        Assert.Equal(2, pane.SelectedIndex);
        Assert.Equal(GitSolarFixtures.BaseSha, pane.SelectedSha);
    }

    [Fact]
    public void FocusCommit_with_an_unknown_sha_changes_nothing()
    {
        using var pane = Loaded(out _);

        Assert.False(pane.Handle(GitSolarCommands.FocusCommit, "ffffffffffffffff"));

        Assert.Equal(0, pane.SelectedIndex);
    }

    [Fact]
    public void FocusBranch_selects_the_branch_tip()
    {
        using var pane = Loaded(out _);

        Assert.True(pane.Handle(GitSolarCommands.FocusBranch, "agent/solar"));

        Assert.Equal(1, pane.SelectedIndex);
        Assert.Equal(GitSolarFixtures.FeatureSha, pane.SelectedSha);
    }

    [Fact]
    public void An_unknown_command_is_ignored()
    {
        using var pane = Loaded(out _);

        Assert.False(pane.Handle("git.unknown-command", "anything"));

        Assert.Equal(0, pane.SelectedIndex);
    }

    [Fact]
    public void Stepping_follows_the_list_and_wraps()
    {
        using var pane = Loaded(out _);

        pane.MoveNext();
        Assert.Equal(1, pane.SelectedIndex);
        pane.MoveNext();
        Assert.Equal(2, pane.SelectedIndex);
        pane.MoveNext();
        Assert.Equal(0, pane.SelectedIndex);
        pane.MovePrevious();
        Assert.Equal(2, pane.SelectedIndex);
    }

    [Fact]
    public void Home_and_End_select_the_first_and_last_rows()
    {
        using var pane = Loaded(out _);

        pane.MoveLast();
        Assert.Equal(2, pane.SelectedIndex);
        pane.MoveFirst();
        Assert.Equal(0, pane.SelectedIndex);
    }

    [Fact]
    public void OpenSelected_raises_the_open_request_for_the_selected_commit()
    {
        using var pane = Loaded(out _);
        var opened = new List<GitSolarOpenCommit>();
        pane.OpenCommitRequested += opened.Add;

        pane.MoveNext();
        var raised = pane.OpenSelected();

        Assert.True(raised);
        var request = Assert.Single(opened);
        Assert.Equal(GitSolarFixtures.FeatureSha, request.Sha);
        Assert.Equal("agent/solar", request.Branch);
    }

    [Fact]
    public void A_null_source_shows_an_empty_state_and_never_throws()
    {
        using var pane = new GitSolarPaneViewModel(GitSolarFixtures.Descriptor(), source: null);

        Assert.Empty(pane.Items);
        Assert.Equal(-1, pane.SelectedIndex);
        Assert.Equal("No solar-system feed is connected.", pane.StatusText);
        pane.MoveNext();
        pane.MovePrevious();
        Assert.False(pane.OpenSelected());
        Assert.False(pane.Handle(GitSolarCommands.FocusCommit, GitSolarFixtures.BaseSha));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"repository\": 3 }")]
    [InlineData("{ \"repository\": \" \" }")]
    [InlineData("[]")]
    public void A_missing_or_bad_repository_raises_a_pane_model_exception(string modelJson)
    {
        var source = new FakeGitSolarSource();

        Assert.Throws<PaneModelException>(() => new GitSolarPaneViewModel(GitSolarFixtures.Descriptor(modelJson), source));
    }

    [Fact]
    public void Dispose_cancels_the_consumer()
    {
        var source = new FakeGitSolarSource();
        var pane = Connected(source);
        Eventually.Until(() => source.Subscribed, "consumer started");

        pane.Dispose();

        Eventually.Until(() => source.Cancelled, "consumer cancelled");
        Assert.Equal("owner/repo", source.Repository);
    }

    [Fact]
    public void A_snapshot_pushed_before_the_pane_is_built_is_still_shown()
    {
        using var pane = WithSnapshot(new FakeGitSolarSource());

        Eventually.Until(() => pane.Items.Count == 3, "buffered snapshot");
        Assert.Equal(GitSolarFixtures.MergeSha, pane.SelectedSha);
    }
}
