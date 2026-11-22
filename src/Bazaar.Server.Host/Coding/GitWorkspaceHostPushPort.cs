using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Workspace;
using Bazaar.Server.CodingGit;
using ModulePullRequest = Bazaar.Server.CodingGit.PullRequestRef;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Binds the harness's <see cref="IHostPushPort"/> to <see cref="GitWorkspace"/> and the GitHub adapter (doc 21). The push
/// token is requested for this push only, narrowed to the one repository, and never leaves the host. The pull request is
/// opened through <see cref="IGitHubAdapter.OpenDraftPrAsync"/>, which only creates drafts, against the repository's default branch.
/// </summary>
public sealed class GitWorkspaceHostPushPort : IHostPushPort
{
    private static readonly IReadOnlyDictionary<string, string> _pushPermissions =
        new Dictionary<string, string> { ["contents"] = "write" };

    private readonly GitWorkspace _workspace;
    private readonly string _root;
    private readonly IGitHubAdapter _github;
    private readonly IGitHubTokenProvider _tokens;
    private readonly ICodingRepoDirectory _repos;
    private readonly string _webBase;

    /// <summary>Creates the port.</summary>
    /// <param name="workspace">The host workspace.</param>
    /// <param name="workspaceRoot">The same root the workspace was configured with.</param>
    /// <param name="github">The GitHub adapter that opens the draft pull request.</param>
    /// <param name="tokens">The installation-token source behind the Identity Service.</param>
    /// <param name="repos">Repository lookup.</param>
    /// <param name="webBase">The GitHub web address used to build the pull request URL.</param>
    public GitWorkspaceHostPushPort(
        GitWorkspace workspace,
        string workspaceRoot,
        IGitHubAdapter github,
        IGitHubTokenProvider tokens,
        ICodingRepoDirectory repos,
        string webBase = "https://github.com")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentException.ThrowIfNullOrWhiteSpace(webBase);
        _workspace = workspace;
        _root = Path.GetFullPath(workspaceRoot);
        _github = github;
        _tokens = tokens;
        _repos = repos;
        _webBase = webBase.TrimEnd('/');
    }

    /// <inheritdoc />
    public async Task<ModulePullRequest> PushDraftPrAsync(HostPushRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var binding = await _repos.FindAsync(request.Repo.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new CodingGitException(CodingGitError.InvalidRequest, "The repository is not known to the host.");

        var taskId = request.Branch.TaskSlug;
        var worktree = new TaskWorktree(request.Repo.Id, taskId, request.Branch.Value, Path.Combine(_root, request.Repo.Id, taskId));
        var scope = new GitHubTokenScope(binding.InstallationId, [binding.RepositoryId], _pushPermissions);

        await _workspace.PushAsync(
            worktree,
            async ct => (await _tokens.GetTokenAsync(scope, ct).ConfigureAwait(false)).Value,
            cancellationToken).ConfigureAwait(false);

        var pr = await _github.OpenDraftPrAsync(
            new OpenPrRequest(
                binding.InstallationId,
                binding.Owner,
                binding.Repo,
                request.Branch.Value,
                request.Repo.DefaultBranch,
                request.Title,
                request.Body),
            cancellationToken).ConfigureAwait(false);

        return new ModulePullRequest(request.Repo.Id, pr.Pr.Number, $"{_webBase}/{pr.Pr.Owner}/{pr.Pr.Repo}/pull/{pr.Pr.Number}", pr.Draft);
    }
}
