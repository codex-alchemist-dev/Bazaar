namespace Bazaar.Contracts.Graph;

/// <summary>
/// One branch of the Git Solar System (doc 19, Git Solar System client module). A branch is an orbit around the default branch.
/// <see cref="TipSha"/> is the commit the branch points at; <see cref="IsAgentBranch"/> marks an <c>agent/&lt;task&gt;</c> planet.
/// </summary>
public sealed record GitSolarBranch(string Name, string TipSha, bool IsAgentBranch = false);

/// <summary>
/// One commit body. <see cref="Branch"/> is the name of the orbit the commit sits on and <see cref="CommittedAt"/> orders the
/// bodies along it. The server's index order is the node order of the matching <see cref="LayoutFrame"/>.
/// </summary>
public sealed record GitSolarCommit(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string Branch,
    DateTimeOffset CommittedAt,
    string Subject = "");

/// <summary>A parent link. <see cref="IsComet"/> is true for a merged branch tip to its merge commit (doc 19, DECIDED 2026-11-11).</summary>
public sealed record GitSolarEdge(string FromSha, string ToSha, bool IsComet);

/// <summary>
/// A versioned copy of the git history for the solar-system view. Positions come separately as a <see cref="LayoutFrame"/>
/// of the same <see cref="GraphVersion"/>, with one entry per commit in <see cref="Commits"/> order.
/// </summary>
public sealed record GitSolarSnapshot(
    long GraphVersion,
    string DefaultBranch,
    IReadOnlyList<GitSolarBranch> Branches,
    IReadOnlyList<GitSolarCommit> Commits,
    IReadOnlyList<GitSolarEdge> Edges);

/// <summary>
/// The body of <c>GET /git/{provider}/repos/{owner}/{repo}/solar</c>: the history and the layout frame of the same graph version
/// (the frame's node order is the snapshot's commit order).
/// </summary>
public sealed record GitSolarFeed(GitSolarSnapshot Snapshot, LayoutFrame Frame);
