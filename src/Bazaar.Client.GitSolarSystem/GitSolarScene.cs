using Bazaar.Constellation.Rendering;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem;

/// <summary>
/// The renderer-ready form of one <see cref="Bazaar.Contracts.Graph.GitSolarSnapshot"/>. Node index i is commit i of the snapshot.
/// <see cref="BranchOfNode"/> is the index into <see cref="BranchNames"/> (also the scene's cluster id). <see cref="CometEdges"/>
/// holds the edge indexes (into the scene's edge list) that are comets. <see cref="Commits"/> keeps the commit bodies for selection.
/// </summary>
public sealed record GitSolarScene(
    long GraphVersion,
    ConstellationScene Scene,
    IReadOnlyList<string> Shas,
    IReadOnlyList<string> BranchNames,
    IReadOnlyList<int> BranchOfNode,
    IReadOnlySet<int> CometEdges,
    IReadOnlyList<GitSolarCommit> Commits);
