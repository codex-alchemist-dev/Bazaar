using Bazaar.Server.GitSolarSystem.Abstractions;
using static Bazaar.Server.GitSolarSystem.Tests.Hist;

namespace Bazaar.Server.GitSolarSystem.Tests;

public sealed class GitSolarFeedServiceTests
{
    private static GitSolarFeedService Service(FakeHistorySource source, FakeLayout layout, FixedSettings? settings = null) =>
        new(source, layout, settings ?? new FixedSettings());

    private static FakeHistorySource Repo(GitSolarHistory history) => new FakeHistorySource().With("acme", "app", history);

    [Fact]
    public async Task Frame_has_three_floats_per_node_with_z_zero_and_matches_the_snapshot_version()
    {
        var layout = new FakeLayout();
        var result = await Service(Repo(Linear(4)), layout).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var feed = result.Feed;
        var xyz = feed.Frame.Xyz.ToArray();
        Assert.Equal(3 * feed.Snapshot.Commits.Count, xyz.Length);
        for (var i = 0; i < feed.Snapshot.Commits.Count; i++)
        {
            Assert.Equal(0f, xyz[i * 3 + 2]);
        }

        Assert.Equal(feed.Snapshot.GraphVersion, feed.Frame.GraphVersion);
        Assert.Equal(1, feed.Frame.LayoutSeq);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Focus_is_the_default_branch_tip_and_the_core_is_its_commit_index()
    {
        var layout = new FakeLayout();
        var result = await Service(Repo(Linear(4)), layout).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        var call = Assert.Single(layout.Calls);
        Assert.Equal(4, call.NodeCount);
        Assert.Equal(3, call.CoreIndex);
        Assert.Equal("c3", result!.Feed.Frame.FocusId);
    }

    [Fact]
    public async Task Ages_reach_the_layout_only_in_age_mode_and_the_newest_commit_has_age_zero()
    {
        var layout = new FakeLayout();
        await Service(Repo(Linear(4)), layout).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        var ages = Assert.Single(layout.Calls).Options.ActivityAges!;
        Assert.Equal(4, ages.Length);
        Assert.Equal(0d, ages[3]);
        Assert.Equal(3d * 60, ages[0]);
    }

    [Fact]
    public async Task Equal_mode_passes_no_ages()
    {
        var layout = new FakeLayout();
        var settings = new FixedSettings { OrbitRadiusMode = GitSolarOrbitRadiusMode.Equal };

        await Service(Repo(Linear(3)), layout, settings).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        var options = Assert.Single(layout.Calls).Options;
        Assert.Null(options.ActivityAges);
        Assert.Equal(GitSolarOrbitRadiusMode.Equal, options.OrbitRadiusMode);
    }

    [Fact]
    public async Task Radius_settings_reach_the_layout_options()
    {
        var layout = new FakeLayout();
        var settings = new FixedSettings { MinOrbitRadius = 12, MaxOrbitRadius = 90 };

        await Service(Repo(Linear(2)), layout, settings).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        var options = Assert.Single(layout.Calls).Options;
        Assert.Equal(12, options.MinOrbitRadius);
        Assert.Equal(90, options.MaxOrbitRadius);
    }

    [Fact]
    public async Task Force_mode_seeds_from_the_graph_version_and_deterministic_mode_uses_a_fixed_seed()
    {
        var deterministic = new FakeLayout();
        await Service(Repo(Linear(3)), deterministic).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(deterministic.Calls).Options.Seed);

        var force = new FakeLayout();
        var result = await Service(Repo(Linear(3)), force, new FixedSettings { LayoutMode = GitSolarLayoutMode.Force })
            .GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);
        Assert.Equal(unchecked((int)result!.Feed.Snapshot.GraphVersion), Assert.Single(force.Calls).Options.Seed);
    }

    [Fact]
    public async Task Unknown_repository_gives_null_and_does_not_lay_out()
    {
        var layout = new FakeLayout();

        var result = await Service(new FakeHistorySource(), layout).GetFeedAsync("github", "nobody", "none", TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(layout.Calls);
    }

    [Fact]
    public async Task Empty_repository_gives_an_empty_frame_without_calling_the_layout()
    {
        var layout = new FakeLayout();

        var result = await Service(Repo(History("main", [], [])), layout).GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        Assert.Empty(layout.Calls);
        Assert.Equal(0, result!.Feed.Frame.Xyz.Length);
        Assert.Equal(string.Empty, result.Feed.Frame.FocusId);
    }

    [Fact]
    public async Task History_over_the_cap_is_cut_to_the_newest_commits_and_marked_truncated()
    {
        var layout = new FakeLayout();

        var result = await Service(Repo(Linear(6)), layout, new FixedSettings { MaxCommits = 2 })
            .GetFeedAsync("github", "acme", "app", TestContext.Current.CancellationToken);

        Assert.True(result!.Truncated);
        Assert.Equal(["c4", "c5"], result.Feed.Snapshot.Commits.Select(c => c.Sha));
        Assert.Equal(2, Assert.Single(layout.Calls).NodeCount);
        Assert.Equal(2 * 3, result.Feed.Frame.Xyz.Length);
    }
}
