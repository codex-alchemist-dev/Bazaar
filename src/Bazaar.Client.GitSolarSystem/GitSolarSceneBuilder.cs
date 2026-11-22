using Bazaar.Constellation.Rendering;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem;

/// <summary>
/// Builds a <see cref="GitSolarScene"/> from one snapshot. Pure logic: no Avalonia, no I/O, deterministic for a given snapshot.
/// Branch names are the default branch first, then the other branches in order of first appearance among
/// <see cref="GitSolarSnapshot.Branches"/> and then <see cref="GitSolarSnapshot.Commits"/>.
/// Edges whose endpoints are not both commits of the snapshot are dropped silently.
/// </summary>
public static class GitSolarSceneBuilder
{
    /// <exception cref="ArgumentException">A commit sha appears more than once.</exception>
    public static GitSolarScene Build(GitSolarSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        GitSolarCommit[] commits = snapshot.Commits.ToArray();
        int nodeCount = commits.Length;
        var shas = new string[nodeCount];
        var indexBySha = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodeCount; i++)
        {
            string sha = commits[i].Sha;
            if (!indexBySha.TryAdd(sha, i))
            {
                throw new ArgumentException($"Duplicate commit sha '{sha}'.", nameof(snapshot));
            }

            shas[i] = sha;
        }

        var branchNames = new List<string>();
        var branchIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int IndexOfBranch(string name)
        {
            if (!branchIndex.TryGetValue(name, out int index))
            {
                index = branchNames.Count;
                branchNames.Add(name);
                branchIndex.Add(name, index);
            }

            return index;
        }

        IndexOfBranch(snapshot.DefaultBranch);
        foreach (GitSolarBranch branch in snapshot.Branches)
        {
            IndexOfBranch(branch.Name);
        }

        var branchOfNode = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            branchOfNode[i] = IndexOfBranch(commits[i].Branch);
        }

        var edgePairs = new List<int>(snapshot.Edges.Count * 2);
        var cometEdges = new HashSet<int>();
        foreach (GitSolarEdge edge in snapshot.Edges)
        {
            if (!indexBySha.TryGetValue(edge.FromSha, out int from) || !indexBySha.TryGetValue(edge.ToSha, out int to))
            {
                continue;
            }

            if (edge.IsComet)
            {
                cometEdges.Add(edgePairs.Count / 2);
            }

            edgePairs.Add(from);
            edgePairs.Add(to);
        }

        var scene = new ConstellationScene(nodeCount, edgePairs.ToArray(), branchOfNode);
        return new GitSolarScene(
            snapshot.GraphVersion,
            scene,
            shas,
            branchNames,
            branchOfNode,
            cometEdges,
            commits);
    }
}
