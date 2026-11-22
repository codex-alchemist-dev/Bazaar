using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Tests;

public sealed class GitSolarSelectionTests
{
    [Fact]
    public void MovesFollowTimeOrderNotServerOrder()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.Linear(("C", 30), ("A", 10), ("B", 20)));
        var selection = new GitSolarSelection(scene);

        var visited = new List<string>();
        while (selection.MoveNext())
        {
            visited.Add(selection.Current!);
        }

        Assert.Equal(new[] { "A", "B", "C" }, visited);
    }

    [Fact]
    public void ShaTieBreaksEqualTimes()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.Linear(("b", 5), ("a", 5), ("c", 5)));
        var selection = new GitSolarSelection(scene);

        selection.MoveNext();
        Assert.Equal("a", selection.Current);
        selection.MoveNext();
        Assert.Equal("b", selection.Current);
    }

    [Fact]
    public void MoveNextClampsAtNewestCommit()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.Linear(("A", 1), ("B", 2)));
        var selection = new GitSolarSelection(scene);
        Assert.True(selection.SelectBySha("B"));

        Assert.False(selection.MoveNext());
        Assert.Equal("B", selection.Current);
    }

    [Fact]
    public void MovePreviousClampsAtOldestCommit()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.Linear(("A", 1), ("B", 2)));
        var selection = new GitSolarSelection(scene);
        Assert.True(selection.SelectBySha("A"));

        Assert.False(selection.MovePrevious());
        Assert.Equal("A", selection.Current);
    }

    [Fact]
    public void NothingSelectedUntilChosen()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.Linear(("A", 1)));
        var selection = new GitSolarSelection(scene);

        Assert.Null(selection.Current);
        Assert.Null(selection.ToOpenCommit());
    }

    [Fact]
    public void UnknownShaReturnsFalseAndKeepsSelection()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.Linear(("A", 1), ("B", 2)));
        var selection = new GitSolarSelection(scene);
        Assert.True(selection.SelectBySha("A"));

        Assert.False(selection.SelectBySha("zzz"));
        Assert.Equal("A", selection.Current);
    }

    [Fact]
    public void SelectBranchTipUsesBranchTip()
    {
        GitSolarSnapshot snapshot = GitSolarFixtures.MergeWithComet();
        var selection = new GitSolarSelection(GitSolarSceneBuilder.Build(snapshot));

        Assert.True(selection.SelectBranchTip("feature", snapshot.Branches));
        Assert.Equal("B", selection.Current);
        Assert.False(selection.SelectBranchTip("missing", snapshot.Branches));
        Assert.Equal("B", selection.Current);
    }

    [Fact]
    public void ToOpenCommitCarriesShaAndBranch()
    {
        GitSolarSnapshot snapshot = GitSolarFixtures.MergeWithComet();
        var selection = new GitSolarSelection(GitSolarSceneBuilder.Build(snapshot));
        Assert.True(selection.SelectBySha("B"));

        GitSolarOpenCommit? open = selection.ToOpenCommit();

        Assert.Equal(new GitSolarOpenCommit("B", "feature"), open);
    }
}
