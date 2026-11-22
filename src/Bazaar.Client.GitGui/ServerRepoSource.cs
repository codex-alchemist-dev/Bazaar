using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui;

/// <summary>
/// <see cref="IGitRepoSource"/> whose repo list comes from the server route <c>GET /git/repos</c> through <see cref="IGitGuiClient"/>
/// (doc 52, Repository list route). The server has no branch or commit route yet, so those lists are empty. A repo with no known
/// default branch gets an empty <see cref="GitRepoRow.DefaultBranch"/>.
/// </summary>
public sealed class ServerRepoSource(IGitGuiClient client) : IGitRepoSource
{
    private readonly IGitGuiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<IReadOnlyList<GitRepoRow>> ListReposAsync(CancellationToken cancellationToken = default)
    {
        var repos = await _client.ListReposAsync(cancellationToken).ConfigureAwait(false);
        return [.. repos.Select(static r => new GitRepoRow(r.Provider, r.FullName, r.DefaultBranch ?? string.Empty))];
    }

    public Task<IReadOnlyList<GitBranchRow>> ListBranchesAsync(GitRepoRow repo, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitBranchRow>>([]);

    public Task<IReadOnlyList<GitCommitRow>> ListCommitsAsync(GitRepoRow repo, string branch, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitCommitRow>>([]);
}
