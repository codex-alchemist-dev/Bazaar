using Bazaar.Server.GitSolarSystem.Abstractions;
using static Bazaar.Server.GitSolarSystem.Tests.Hist;

namespace Bazaar.Server.GitSolarSystem.Tests;

public sealed class GitSolarSnapshotBuilderTests
{
    [Fact]
    public void Linear_history_puts_every_commit_on_the_default_branch_with_plain_edges()
    {
        var snapshot = GitSolarSnapshotBuilder.Build(Linear(3));

        Assert.Equal("main", snapshot.DefaultBranch);
        Assert.Equal(["c0", "c1", "c2"], snapshot.Commits.Select(c => c.Sha));
        Assert.All(snapshot.Commits, c => Assert.Equal("main", c.Branch));
        Assert.Equal(
            [("c0", "c1", false), ("c1", "c2", false)],
            snapshot.Edges.Select(e => (e.FromSha, e.ToSha, e.IsComet)));
    }

    [Fact]
    public void Merge_of_a_feature_branch_draws_a_comet_from_the_merged_tip_and_keeps_the_first_parent_edge()
    {
        // main: b0 <- m1 <- m2 <- m3 (merge of f1). feature/x: f0 <- f1, both forked from b0.
        var history = History(
            "main",
            [Ref("main", "m3"), Ref("feature/x", "f1")],
            [
                C("b0", 0),
                C("m1", 1, "b0"),
                C("f0", 2, "b0"),
                C("f1", 3, "f0"),
                C("m2", 4, "m1"),
                C("m3", 5, "m2", "f1"),
            ]);

        var snapshot = GitSolarSnapshotBuilder.Build(history);

        var branchOf = snapshot.Commits.ToDictionary(c => c.Sha, c => c.Branch);
        Assert.Equal("feature/x", branchOf["f0"]);
        Assert.Equal("feature/x", branchOf["f1"]);
        Assert.Equal("main", branchOf["m3"]);
        Assert.Equal("main", branchOf["b0"]);

        var comet = snapshot.Edges.Single(e => e.IsComet);
        Assert.Equal(("f1", "m3"), (comet.FromSha, comet.ToSha));
        Assert.Contains(snapshot.Edges, e => e.FromSha == "m2" && e.ToSha == "m3" && !e.IsComet);
    }

    [Fact]
    public void Agent_branches_keep_their_flag_and_the_default_branch_is_listed_first()
    {
        var history = History(
            "main",
            [Ref("agent/wp-12", "a1", agent: true), Ref("main", "m1"), Ref("alpha", "x1")],
            [C("base", 0), C("m1", 1, "base"), C("x1", 2, "base"), C("a1", 3, "base")]);

        var snapshot = GitSolarSnapshotBuilder.Build(history);

        Assert.Equal(["main", "agent/wp-12", "alpha"], snapshot.Branches.Select(b => b.Name));
        Assert.Equal([false, true, false], snapshot.Branches.Select(b => b.IsAgentBranch));
        Assert.Equal("agent/wp-12", snapshot.Commits.Single(c => c.Sha == "a1").Branch);
    }

    [Fact]
    public void Orphan_commit_no_branch_reaches_goes_to_the_default_branch()
    {
        var history = History(
            "main",
            [Ref("main", "m1")],
            [C("base", 0), C("m1", 1, "base"), C("orphan", 2)]);

        var snapshot = GitSolarSnapshotBuilder.Build(history);

        Assert.Equal("main", snapshot.Commits.Single(c => c.Sha == "orphan").Branch);
        Assert.DoesNotContain(snapshot.Edges, e => e.ToSha == "orphan" || e.FromSha == "orphan");
    }

    [Fact]
    public void Commits_are_ordered_by_time_then_sha_whatever_the_input_order()
    {
        var forward = History("main", [Ref("main", "t")], [C("a", 1), C("b", 1), C("c", 0), C("t", 2, "a")]);
        var reversed = History("main", [Ref("main", "t")], [C("t", 2, "a"), C("b", 1), C("a", 1), C("c", 0)]);

        var first = GitSolarSnapshotBuilder.Build(forward);
        var second = GitSolarSnapshotBuilder.Build(reversed);

        Assert.Equal(["c", "a", "b", "t"], first.Commits.Select(c => c.Sha));
        Assert.Equal(first.Commits.Select(c => (c.Sha, c.Branch)), second.Commits.Select(c => (c.Sha, c.Branch)));
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.GraphVersion, second.GraphVersion);
    }

    [Fact]
    public void Building_twice_gives_the_same_snapshot()
    {
        var history = Linear(5);

        var first = GitSolarSnapshotBuilder.Build(history);
        var second = GitSolarSnapshotBuilder.Build(history);

        Assert.Equal(first.GraphVersion, second.GraphVersion);
        Assert.Equal(first.Edges, second.Edges);
        Assert.Equal(first.Commits.Select(c => c.Sha), second.Commits.Select(c => c.Sha));
    }

    [Fact]
    public void Graph_version_is_non_negative_and_stable_while_history_does_not_change()
    {
        var a = GitSolarSnapshotBuilder.Build(Linear(4));
        var sameHistoryDifferentSubjects = Linear(4) with
        {
            Commits = Linear(4).Commits.Select(c => c with { Subject = "reworded" }).ToList(),
        };

        Assert.True(a.GraphVersion >= 0);
        Assert.Equal(a.GraphVersion, GitSolarSnapshotBuilder.Build(sameHistoryDifferentSubjects).GraphVersion);
    }

    [Fact]
    public void Graph_version_changes_when_a_tip_moves_or_a_commit_is_added()
    {
        var before = GitSolarSnapshotBuilder.Build(Linear(3)).GraphVersion;
        var grown = GitSolarSnapshotBuilder.Build(Linear(4)).GraphVersion;

        var moved = Linear(3) with { Refs = [Ref("main", "c1")] };
        var movedVersion = GitSolarSnapshotBuilder.Build(moved).GraphVersion;

        Assert.NotEqual(before, grown);
        Assert.NotEqual(before, movedVersion);
    }

    [Fact]
    public void A_parent_outside_the_history_gives_no_edge()
    {
        var history = History("main", [Ref("main", "c1")], [C("c1", 1, "cut-off-parent")]);

        var snapshot = GitSolarSnapshotBuilder.Build(history);

        Assert.Empty(snapshot.Edges);
        Assert.Equal("main", snapshot.Commits.Single().Branch);
    }

    [Fact]
    public void An_empty_repository_builds_an_empty_snapshot()
    {
        var snapshot = GitSolarSnapshotBuilder.Build(History("main", [], []));

        Assert.Empty(snapshot.Commits);
        Assert.Empty(snapshot.Edges);
        Assert.Empty(snapshot.Branches);
    }
}
