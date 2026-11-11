namespace Bazaar.Server.Constellation.GitDag;

/// <summary>
/// One commit as the Git Solar System sees it (docs/design/19, Git Solar System). <see cref="Sha"/> is the node id.
/// <see cref="ParentShas"/> keeps git's parent order: the first parent is the line the commit continues.
/// </summary>
public sealed record GitDagNode(string Sha, IReadOnlyList<string> ParentShas, DateTimeOffset CommittedAt);

/// <summary>
/// A parent link between two commits. <see cref="FromSha"/> is the parent and <see cref="ToSha"/> the child, so every
/// edge points forward in history. A comet edge (<see cref="IsComet"/>) comes from the tip of a merged branch
/// (a non-first parent of a merge commit) to the merge commit, its target. All other edges are plain parent links.
/// </summary>
public sealed record GitDagEdge(string FromSha, string ToSha, bool IsComet);

/// <summary>
/// The indexed history reachable from one tip. <see cref="Nodes"/> is sorted by commit time, then by SHA. <see cref="Edges"/>
/// is sorted by target SHA, then source SHA, then plain edges before comets. Both orders are ordinal, so the same history
/// always produces the same output.
/// </summary>
public sealed record GitDagIndex(IReadOnlyList<GitDagNode> Nodes, IReadOnlyList<GitDagEdge> Edges)
{
    /// <summary>The index of a repository with no commits.</summary>
    public static GitDagIndex Empty { get; } = new([], []);
}
