using Bazaar.Contracts.Graph;
using Bazaar.Server.GitSolarSystem.Abstractions;

namespace Bazaar.Server.GitSolarSystem;

/// <summary>
/// Builds the <see cref="GitSolarSnapshot"/> of one repository (doc 19, Git Solar System). Pure: no I/O, no clock, no randomness.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Commits are ordered by commit time, then by sha (ordinal).</description></item>
/// <item><description>Each commit is assigned to the branch whose first-parent walk from its tip reaches it. The default branch
/// claims first, then the other branches in name order (ordinal). A commit no walk reaches goes to the default branch.</description></item>
/// <item><description>Edges are every parent link whose parent is in the history, from parent to child. A comet (<see cref="GitSolarEdge.IsComet"/>)
/// is a link from a non-first parent to a child with more than one parent (DECIDED 2026-11-11).</description></item>
/// <item><description><see cref="GitSolarSnapshot.GraphVersion"/> is a 63-bit FNV-1a hash of the sorted branch tips and the commit count.
/// It changes only when those change.</description></item>
/// </list>
/// </remarks>
public static class GitSolarSnapshotBuilder
{
    private const ulong _fnvOffset = 14695981039346656037UL;
    private const ulong _fnvPrime = 1099511628211UL;

    /// <summary>Builds the snapshot of <paramref name="history"/>.</summary>
    public static GitSolarSnapshot Build(GitSolarHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var commits = history.Commits
            .OrderBy(c => c.CommittedAt.ToUniversalTime())
            .ThenBy(c => c.Sha, StringComparer.Ordinal)
            .ToList();
        var indexBySha = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < commits.Count; i++)
        {
            indexBySha.TryAdd(commits[i].Sha, i);
        }

        var branches = OrderedBranches(history);
        var branchOf = AssignBranches(history.DefaultBranch, branches, commits, indexBySha);

        var snapshotCommits = new List<GitSolarCommit>(commits.Count);
        var edges = new List<GitSolarEdge>();
        for (var i = 0; i < commits.Count; i++)
        {
            var commit = commits[i];
            snapshotCommits.Add(new GitSolarCommit(commit.Sha, commit.ParentShas.ToList(), branchOf[i], commit.CommittedAt, commit.Subject));

            var isMerge = commit.ParentShas.Count > 1;
            for (var p = 0; p < commit.ParentShas.Count; p++)
            {
                var parent = commit.ParentShas[p];
                if (!indexBySha.ContainsKey(parent))
                {
                    continue;
                }

                edges.Add(new GitSolarEdge(parent, commit.Sha, IsComet: isMerge && p > 0));
            }
        }

        var snapshotBranches = branches
            .Select(b => new GitSolarBranch(b.Name, b.TipSha, b.IsAgentBranch))
            .ToList();

        return new GitSolarSnapshot(
            GraphVersion: GraphVersion(branches, commits.Count),
            DefaultBranch: history.DefaultBranch,
            Branches: snapshotBranches,
            Commits: snapshotCommits,
            Edges: edges);
    }

    /// <summary>Branches in claim order: the default branch first, then the rest by name (ordinal).</summary>
    private static List<GitSolarRef> OrderedBranches(GitSolarHistory history)
    {
        return history.Refs
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Name == history.DefaultBranch ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns the branch name per commit index. Unreached commits go to the default branch.</summary>
    private static string[] AssignBranches(
        string defaultBranch,
        List<GitSolarRef> branches,
        List<GitSolarHistoryCommit> commits,
        Dictionary<string, int> indexBySha)
    {
        var assigned = new string?[commits.Count];
        foreach (var branch in branches)
        {
            if (!indexBySha.TryGetValue(branch.TipSha, out var at))
            {
                continue;
            }

            // A commit already claimed by an earlier branch carries its whole first-parent chain, so the walk can stop there.
            while (true)
            {
                if (assigned[at] is not null)
                {
                    break;
                }

                assigned[at] = branch.Name;
                var firstParent = commits[at].ParentShas.Count > 0 ? commits[at].ParentShas[0] : null;
                if (firstParent is null || !indexBySha.TryGetValue(firstParent, out var next))
                {
                    break;
                }

                at = next;
            }
        }

        return assigned.Select(name => name ?? defaultBranch).ToArray();
    }

    /// <summary>FNV-1a over the sorted tips and the commit count, masked to 63 bits so it fits a non-negative long.</summary>
    private static long GraphVersion(List<GitSolarRef> branches, int commitCount)
    {
        var hash = _fnvOffset;
        foreach (var branch in branches.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            hash = Mix(hash, branch.Name + "=" + branch.TipSha + "\n");
        }

        hash = Mix(hash, "count=" + commitCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return unchecked((long)(hash & long.MaxValue));
    }

    private static ulong Mix(ulong hash, string text)
    {
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash = unchecked(hash * _fnvPrime);
        }

        return hash;
    }
}
