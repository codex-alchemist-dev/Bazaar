using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Workspace;
using Bazaar.Server.CodingGit;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Vault.Git;
using Repository = LibGit2Sharp.Repository;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class GitWorkspaceAdaptersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-coding-git-" + Guid.NewGuid().ToString("N"));

    public GitWorkspaceAdaptersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            ForceDelete(_root);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }

    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class FakeProvider : IGitProvider
    {
        public (string Repo, string Branch, string Path)? Added { get; private set; }

        public (string Checkout, string Message, GitIdentity Author)? Committed { get; private set; }

        public Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct)
        {
            Added = (repoPath, branch, worktreePath);
            return Task.FromResult(worktreePath);
        }

        public Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct)
        {
            Committed = (checkoutPath, message, author);
            return Task.FromResult("abc123");
        }

        public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();

        public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null) =>
            throw new NotImplementedException();

        public Task<string> HeadAsync(string checkoutPath, CancellationToken ct) => throw new NotImplementedException();

        public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) => throw new NotImplementedException();

        public Task<string> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct) => throw new NotImplementedException();

        public Task<string> ReadTextAsync(string repoPath, string commitSha, string path, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<GitCommitInfo>> ListCommitsAsync(string repoPath, string? excludeSha, string includeSha, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeRepos(CodingRepoBinding? binding) : ICodingRepoDirectory
    {
        public ValueTask<CodingRepoBinding?> FindAsync(string repoId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(repoId == "repo1" ? binding : null);
    }

    private sealed class CountingTokens : IGitHubTokenProvider
    {
        public int Calls { get; private set; }

        public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GitHubToken("tok", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class CountingGitHub : IGitHubAdapter
    {
        public int Opened { get; private set; }

        public Task<GitHub.Adapter.PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken)
        {
            Opened++;
            return Task.FromResult(new GitHub.Adapter.PullRequestRef(new PrRef(request.InstallationId, request.Owner, request.Repo, 7), "node", "sha", Draft: true));
        }

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private static readonly CodingRepoBinding _binding = new("/srv/repos/repo1", 11, "acme", "repo1", 99);
    private static readonly RepoRef _repo = new("repo1", "main");

    private (GitWorkspaceCodingGitEngine Engine, FakeProvider Provider) MakeEngine(CodingRepoBinding? binding = null)
    {
        var provider = new FakeProvider();
        var workspace = new GitWorkspace(provider, new GitWorkspaceOptions { RootPath = _root });
        return (new GitWorkspaceCodingGitEngine(workspace, _root, new FakeRepos(binding ?? _binding)), provider);
    }

    [Fact]
    public async Task Add_worktree_creates_the_task_worktree_on_the_agent_branch()
    {
        var (engine, provider) = MakeEngine();
        var path = Path.Combine(_root, "repo1", "fix-x");

        await engine.AddWorktreeAsync(new AddWorktreeRequest(_repo, BranchName.ForTask("fix-x"), path), CancellationToken.None);

        Assert.Equal((_binding.RepoPath, "agent/fix-x", path), provider.Added);
    }

    [Theory]
    [InlineData("elsewhere/repo1/fix-x")]
    [InlineData("repo1/other-task")]
    [InlineData("repo2/fix-x")]
    [InlineData("repo1/fix-x/deeper")]
    public async Task Add_worktree_refuses_a_path_that_is_not_the_workspace_layout_for_the_branch(string relative)
    {
        var (engine, provider) = MakeEngine();
        var path = relative.StartsWith("elsewhere", StringComparison.Ordinal)
            ? Path.Combine(Path.GetTempPath(), relative)
            : Path.Combine(_root, relative);

        await Assert.ThrowsAsync<CodingGitException>(() =>
            engine.AddWorktreeAsync(new AddWorktreeRequest(_repo, BranchName.ForTask("fix-x"), path), CancellationToken.None));
        Assert.Null(provider.Added);
    }

    [Fact]
    public async Task Add_worktree_refuses_an_unknown_repository()
    {
        var provider = new FakeProvider();
        var workspace = new GitWorkspace(provider, new GitWorkspaceOptions { RootPath = _root });
        var engine = new GitWorkspaceCodingGitEngine(workspace, _root, new FakeRepos(null));

        var ex = await Assert.ThrowsAsync<CodingGitException>(() =>
            engine.AddWorktreeAsync(new AddWorktreeRequest(_repo, BranchName.ForTask("fix-x"), Path.Combine(_root, "repo1", "fix-x")), CancellationToken.None));

        Assert.Equal(CodingGitError.InvalidRequest, ex.Error);
        Assert.Null(provider.Added);
    }

    [Fact]
    public async Task Commit_maps_trailers_and_author_and_returns_the_sha()
    {
        var (engine, provider) = MakeEngine();
        var path = Path.Combine(_root, "repo1", "fix-x");

        var id = await engine.CommitAsync(
            new EngineCommitRequest(
                path,
                "Fix x",
                new CommitTrailers("agent-1", "gc-1", "fix-x", "Claude <noreply@anthropic.com>"),
                new GitAuthor("bot", "bot@example.invalid")),
            CancellationToken.None);

        Assert.Equal("abc123", id.Sha);
        var (checkout, message, author) = provider.Committed!.Value;
        Assert.Equal(path, checkout);
        Assert.Equal(new GitIdentity("bot", "bot@example.invalid"), author);
        Assert.Contains("Bazaar-Agent: agent-1", message, StringComparison.Ordinal);
        Assert.Contains("Bazaar-GC: gc-1", message, StringComparison.Ordinal);
        Assert.Contains("Bazaar-Task: fix-x", message, StringComparison.Ordinal);
        Assert.Contains("Co-Authored-By: Claude <noreply@anthropic.com>", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Signed-off-by", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Commit_outside_the_workspace_is_refused()
    {
        var (engine, provider) = MakeEngine();

        await Assert.ThrowsAsync<CodingGitException>(() =>
            engine.CommitAsync(
                new EngineCommitRequest(
                    Path.Combine(Path.GetTempPath(), "x", "y"),
                    "m",
                    new CommitTrailers("a", "g", "t", "c"),
                    new GitAuthor("n", "e@example.invalid")),
                CancellationToken.None));
        Assert.Null(provider.Committed);
    }

    private static string InitWorktreeRepo(string path)
    {
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        LibGit2Sharp.Commands.Stage(repo, "a.txt");
        var sig = new LibGit2Sharp.Signature("bot", "bot@example.invalid", DateTimeOffset.UtcNow);
        LibGit2Sharp.RepositoryExtensions.Commit(repo, "base", sig, sig);
        return repo.Head.Tip.Sha;
    }

    [Fact]
    public async Task Diff_returns_changed_paths_and_a_unified_patch()
    {
        var (engine, _) = MakeEngine();
        var path = Path.Combine(_root, "repo1", "fix-x");
        var baseSha = InitWorktreeRepo(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\ntwo\n");

        var diff = await engine.DiffAgainstBaseAsync(path, baseSha, CancellationToken.None);

        Assert.Equal(["a.txt"], diff.ChangedPaths);
        Assert.Contains("+two", diff.Patch, StringComparison.Ordinal);
        Assert.Equal(baseSha, diff.BaseBranch);
    }

    [Fact]
    public async Task Diff_outside_the_workspace_is_refused()
    {
        var (engine, _) = MakeEngine();

        await Assert.ThrowsAsync<CodingGitException>(() =>
            engine.DiffAgainstBaseAsync(Path.Combine(Path.GetTempPath(), "x", "y"), "main", CancellationToken.None));
    }

    private sealed class FakeLayout(string root) : IWorkShareLayout
    {
        public string GetWorktreeRoot(string agentId) => Path.Combine(root, agentId);

        public string GetPassportFolder(string agentId) => Path.Combine(root, agentId + "-passport");
    }

    [Fact]
    public async Task Per_agent_roots_place_commit_and_diff_under_the_agents_share()
    {
        var provider = new FakeProvider();
        var layout = new FakeLayout(_root);
        var workspace = new GitWorkspace(provider, new GitWorkspaceOptions { RootPath = _root }, new WorkShareLayoutRootResolver(layout));
        var engine = new GitWorkspaceCodingGitEngine(workspace, _root, new FakeRepos(_binding), layout);
        var path = Path.Combine(_root, "agent-1", "repo1", "fix-x");

        await engine.AddWorktreeAsync(new AddWorktreeRequest(_repo, BranchName.ForTask("fix-x"), path, "agent-1"), CancellationToken.None);
        var id = await engine.CommitAsync(
            new EngineCommitRequest(path, "m", new CommitTrailers("agent-1", "g", "fix-x", "c"), new GitAuthor("n", "e@example.invalid")),
            CancellationToken.None);

        Assert.Equal((_binding.RepoPath, "agent/fix-x", path), provider.Added);
        Assert.Equal("abc123", id.Sha);

        // The global-root layout is not valid for an agent that has its own share.
        await Assert.ThrowsAsync<CodingGitException>(() =>
            engine.AddWorktreeAsync(new AddWorktreeRequest(_repo, BranchName.ForTask("fix-y"), Path.Combine(_root, "repo1", "fix-y"), "agent-1"), CancellationToken.None));
    }

    private GitWorkspaceHostPushPort MakePush(CountingTokens tokens, CountingGitHub github, CodingRepoBinding? binding = null) =>
        new(
            new GitWorkspace(new FakeProvider(), new GitWorkspaceOptions { RootPath = _root }),
            _root,
            github,
            tokens,
            new FakeRepos(binding ?? _binding));

    [Fact]
    public async Task Push_to_an_origin_that_is_not_github_asks_for_no_token_and_opens_no_pull_request()
    {
        var path = Path.Combine(_root, "repo1", "fix-x");
        Repository.Init(path);
        using (var repo = new Repository(path))
        {
            repo.Network.Remotes.Add("origin", "https://evil.example.invalid/acme/repo1.git");
        }

        var tokens = new CountingTokens();
        var github = new CountingGitHub();
        var port = MakePush(tokens, github);

        await Assert.ThrowsAsync<GitWorkspaceException>(() =>
            port.PushDraftPrAsync(new HostPushRequest("agent-1", _repo, BranchName.ForTask("fix-x"), "t", "b"), CancellationToken.None));

        Assert.Equal(0, tokens.Calls);
        Assert.Equal(0, github.Opened);
    }

    [Fact]
    public async Task Push_for_an_unknown_repository_is_refused_before_any_credential_or_pull_request()
    {
        var tokens = new CountingTokens();
        var github = new CountingGitHub();
        var port = MakePush(tokens, github, binding: null);
        var unknown = new RepoRef("repo9", "main");

        var ex = await Assert.ThrowsAsync<CodingGitException>(() =>
            port.PushDraftPrAsync(new HostPushRequest("agent-1", unknown, BranchName.ForTask("fix-x"), "t", "b"), CancellationToken.None));

        Assert.Equal(CodingGitError.InvalidRequest, ex.Error);
        Assert.Equal(0, tokens.Calls);
        Assert.Equal(0, github.Opened);
    }
}
