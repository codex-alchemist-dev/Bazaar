using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Tests;

public sealed class GitSolarSceneBuilderTests
{
    [Fact]
    public void MergeWithCometKeepsPlainAndCometEdges()
    {
        GitSolarScene scene = GitSolarSceneBuilder.Build(GitSolarFixtures.MergeWithComet());

        Assert.Equal(3, scene.Scene.NodeCount);
        Assert.Equal(new[] { 2, 0, 1, 0, 1, 2 }, scene.Scene.EdgePairs.ToArray());
        Assert.Equal(new[] { 2 }, scene.CometEdges.ToArray());
        Assert.Equal(new[] { "A", "B", "M" }, scene.Shas);
    }

    [Fact]
    public void DanglingEdgeIsSkippedWithoutShiftingCometIndexes()
    {
        GitSolarSnapshot baseline = GitSolarFixtures.MergeWithComet();
        GitSolarSnapshot snapshot = baseline with
        {
            Edges =
            [
                new GitSolarEdge("ghost", "A", IsComet: false),
                new GitSolarEdge("B", "M", IsComet: true),
                new GitSolarEdge("M", "nowhere", IsComet: true),
            ],
        };

        GitSolarScene scene = GitSolarSceneBuilder.Build(snapshot);

        Assert.Equal(1, scene.Scene.EdgeCount);
        Assert.Equal(new[] { 1, 2 }, scene.Scene.EdgePairs.ToArray());
        Assert.Equal(new[] { 0 }, scene.CometEdges.ToArray());
    }

    [Fact]
    public void UnknownBranchIsAppendedAfterListedBranches()
    {
        GitSolarSnapshot snapshot = new(
            1,
            "main",
            [new GitSolarBranch("feature", "B"), new GitSolarBranch("main", "A")],
            [
                GitSolarFixtures.Commit("A", "main", 1),
                GitSolarFixtures.Commit("H", "hotfix", 2, "A"),
                GitSolarFixtures.Commit("B", "feature", 3, "A"),
            ],
            []);

        GitSolarScene scene = GitSolarSceneBuilder.Build(snapshot);

        Assert.Equal(new[] { "main", "feature", "hotfix" }, scene.BranchNames);
        Assert.Equal(new[] { 0, 2, 1 }, scene.BranchOfNode);
        Assert.Equal(scene.BranchOfNode.ToArray(), scene.Scene.ClusterIds.ToArray());
    }

    [Fact]
    public void DuplicateShaThrows()
    {
        GitSolarSnapshot snapshot = GitSolarFixtures.Linear(("A", 1), ("A", 2));

        Assert.Throws<ArgumentException>(() => GitSolarSceneBuilder.Build(snapshot));
    }

    [Fact]
    public void BuildIsDeterministic()
    {
        GitSolarSnapshot snapshot = GitSolarFixtures.MergeWithComet();

        GitSolarScene first = GitSolarSceneBuilder.Build(snapshot);
        GitSolarScene second = GitSolarSceneBuilder.Build(snapshot);

        Assert.Equal(first.Shas, second.Shas);
        Assert.Equal(first.BranchNames, second.BranchNames);
        Assert.Equal(first.BranchOfNode, second.BranchOfNode);
        Assert.Equal(first.CometEdges, second.CometEdges);
        Assert.Equal(first.Scene.EdgePairs.ToArray(), second.Scene.EdgePairs.ToArray());
    }

    [Fact]
    public void DefaultBranchComesFirstWhateverTheBranchOrder()
    {
        GitSolarSnapshot snapshot = GitSolarFixtures.MergeWithComet() with
        {
            Branches = [new GitSolarBranch("feature", "B"), new GitSolarBranch("main", "M")],
        };

        GitSolarScene scene = GitSolarSceneBuilder.Build(snapshot);

        Assert.Equal(new[] { "main", "feature" }, scene.BranchNames);
        Assert.Equal(new[] { 0, 1, 0 }, scene.BranchOfNode);
    }

    [Fact]
    public void EmptySnapshotBuildsEmptyScene()
    {
        GitSolarSnapshot snapshot = new(7, "main", [], [], []);

        GitSolarScene scene = GitSolarSceneBuilder.Build(snapshot);

        Assert.Equal(7, scene.GraphVersion);
        Assert.Equal(0, scene.Scene.NodeCount);
        Assert.Empty(scene.Commits);
        Assert.Equal(new[] { "main" }, scene.BranchNames);
    }
}
