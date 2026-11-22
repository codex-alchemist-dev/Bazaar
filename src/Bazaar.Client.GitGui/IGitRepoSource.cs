namespace Bazaar.Client.GitGui;

/// <summary>A repository row in the Git view.</summary>
public sealed record GitRepoRow(string Provider, string FullName, string DefaultBranch);

/// <summary>A branch row: its name and head commit.</summary>
public sealed record GitBranchRow(string Name, string HeadSha);

/// <summary>A recent commit row.</summary>
public sealed record GitCommitRow(string Sha, string Summary, string AuthorLogin, long CommittedAtMs);

/// <summary>
/// The seam to the Git view's repo, branch and commit lists (doc 52). Doc 52 defines no wire route for these yet, so this is a
/// seam only; a head or a later change supplies the implementation.
/// </summary>
public interface IGitRepoSource
{
    Task<IReadOnlyList<GitRepoRow>> ListReposAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranchRow>> ListBranchesAsync(GitRepoRow repo, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommitRow>> ListCommitsAsync(GitRepoRow repo, string branch, CancellationToken cancellationToken = default);
}
