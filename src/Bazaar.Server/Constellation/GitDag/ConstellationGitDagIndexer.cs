using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Constellation.GitDag;

/// <summary>
/// Builds the commit DAG the Git Solar System draws, reading history only through <see cref="IGitProvider"/> (doc 19,
/// work package 6). It never shells out to git. Each commit becomes a node. Each parent link becomes an edge. For a merge
/// commit, every parent after the first is a merged branch tip and becomes a comet edge to the merge commit.
/// </summary>
public sealed class ConstellationGitDagIndexer
{
    private readonly IGitProvider _git;

    public ConstellationGitDagIndexer(IGitProvider git)
    {
        ArgumentNullException.ThrowIfNull(git);

        _git = git;
    }

    /// <summary>
    /// Indexes every commit reachable from <paramref name="tipCommitish"/> (a branch, tag or SHA). A null or blank tip means
    /// the repository has no commits yet, so the result is empty. Throws when the tip does not resolve.
    /// </summary>
    public async Task<GitDagIndex> IndexAsync(string repoPath, string? tipCommitish, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        if (string.IsNullOrWhiteSpace(tipCommitish))
        {
            return GitDagIndex.Empty;
        }

        var tip = await _git.ResolveCommitAsync(repoPath, tipCommitish, ct);
        var commits = await _git.ListCommitsAsync(repoPath, null, tip, ct);

        var nodes = new List<GitDagNode>(commits.Count);
        var edges = new List<GitDagEdge>();
        foreach (var commit in commits)
        {
            nodes.Add(new GitDagNode(commit.Sha, commit.Parents.ToArray(), commit.CommittedAt));

            for (var i = 0; i < commit.Parents.Count; i++)
            {
                // Index 0 is the line the commit continues. Any later parent is a merged branch tip, so it is a comet.
                edges.Add(new GitDagEdge(commit.Parents[i], commit.Sha, IsComet: i > 0));
            }
        }

        nodes.Sort(static (a, b) =>
        {
            var byTime = a.CommittedAt.CompareTo(b.CommittedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Sha, b.Sha);
        });

        edges.Sort(static (a, b) =>
        {
            var byTarget = string.CompareOrdinal(a.ToSha, b.ToSha);
            if (byTarget != 0)
            {
                return byTarget;
            }

            var bySource = string.CompareOrdinal(a.FromSha, b.FromSha);
            if (bySource != 0)
            {
                return bySource;
            }

            return a.IsComet.CompareTo(b.IsComet);
        });

        return new GitDagIndex(nodes, edges);
    }
}
