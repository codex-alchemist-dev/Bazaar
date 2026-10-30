using Bazaar.Server.GitHub.Workspace;
using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.GitHub.Tests.Workspace;

public sealed class GitWorkspaceTests : IDisposable
{
    private static readonly GitIdentity _bot = new("codex-alchemist-dev[bot]", "1+codex-alchemist-dev[bot]@users.noreply.github.com");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-wp7-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitProvider _git = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private GitWorkspace CreateWorkspace(IGitProvider? git = null) =>
        new(git ?? _git, new GitWorkspaceOptions { RootPath = Path.Combine(_root, "worktrees") });

    /// <summary>A worktree at the path this workspace would create for the ids.</summary>
    private TaskWorktree Worktree(string repoId, string taskId) =>
        new(repoId, taskId, "agent/" + taskId, Path.Combine(_root, "worktrees", repoId, taskId));

    private static CommitRequest Request(string message = "Update notes") =>
        new(message, _bot, "agent-1", "gc-1", "task-1", "Claude Haiku 5.5 <noreply@anthropic.com>");

    private static void InitRepo(string path, string? originUrl)
    {
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        var signature = new Signature(_bot.Name, _bot.Email, DateTimeOffset.UtcNow);
        repo.Commit("Initial commit", signature, signature, new CommitOptions { AllowEmptyCommit = true });
        if (originUrl is not null)
        {
            repo.Network.Remotes.Add("origin", originUrl);
        }
    }

    [Fact]
    public async Task Refuses_default_branch_before_asking_for_credential()
    {
        var workspace = CreateWorkspace();
        var credentialRequested = false;
        var worktree = new TaskWorktree("repo-a", "task-1", "main", Path.Combine(_root, "worktrees", "repo-a", "task-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.PushAsync(
            worktree,
            ct =>
            {
                credentialRequested = true;
                return Task.FromResult("token");
            },
            TestContext.Current.CancellationToken));

        Assert.False(credentialRequested);
    }

    [Fact]
    public async Task Refuses_branches_outside_agent_prefix()
    {
        var workspace = CreateWorkspace();
        string[] refused = ["feature/login", "refs/heads/main", "agent/", "agent/../main", "agentx/task", " agent/task"];

        foreach (var branch in refused)
        {
            var worktree = new TaskWorktree("repo-a", "task-1", branch, Path.Combine(_root, "worktrees", "repo-a", "task-1"));
            await Assert.ThrowsAsync<ArgumentException>(() => workspace.PushAsync(
                worktree,
                _ => Task.FromResult("token"),
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Refuses_empty_commit_message()
    {
        var workspace = CreateWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CommitAsync(
            Worktree("repo-a", "task-1"),
            Request("   "),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _git.CommitCount);
    }

    [Fact]
    public async Task Refuses_signed_off_by_in_commit_message()
    {
        var workspace = CreateWorkspace();
        var worktree = Worktree("repo-a", "task-1");

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CommitAsync(
            worktree,
            Request("Fix parser\n\nSigned-off-by: Someone <someone@example.com>"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CommitAsync(
            worktree,
            Request("Fix parser\n\nsigned-off-by: someone <someone@example.com>"),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _git.CommitCount);
    }

    [Fact]
    public async Task Commit_writes_bot_author_and_bazaar_trailers()
    {
        var workspace = CreateWorkspace();

        var sha = await workspace.CommitAsync(Worktree("repo-a", "task-1"), Request(), TestContext.Current.CancellationToken);

        Assert.Equal("fake-sha-1", sha);
        var commit = Assert.Single(_git.Commits);
        Assert.Equal(_bot, commit.Author);
        Assert.Equal(
            "Update notes\n\n" +
            "Co-Authored-By: Claude Haiku 5.5 <noreply@anthropic.com>\n" +
            "Bazaar-Agent: agent-1\n" +
            "Bazaar-GC: gc-1\n" +
            "Bazaar-Task: task-1",
            commit.Message);
    }

    [Fact]
    public async Task Operations_on_one_repo_never_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();

        await Task.WhenAll(
            workspace.CommitAsync(Worktree("repo-a", "task-1"), Request("one"), ct),
            workspace.CommitAsync(Worktree("repo-a", "task-2"), Request("two"), ct),
            workspace.CreateTaskWorktreeAsync("repo-a", Path.Combine(_root, "repo-a"), "task-3", ct),
            workspace.CommitAsync(Worktree("repo-a", "task-1"), Request("three"), ct));

        Assert.Equal(3, _git.CommitCount);
        Assert.Equal(1, _git.MaxActive);
    }

    [Fact]
    public async Task Two_ids_for_one_repo_path_never_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();
        var repoPath = Path.Combine(_root, "shared-repo");

        var first = await workspace.CreateTaskWorktreeAsync("id-a", repoPath, "task-1", ct);
        await Task.WhenAll(
            workspace.CommitAsync(first, Request("one"), ct),
            workspace.CreateTaskWorktreeAsync("id-b", repoPath, "task-2", ct));

        Assert.Equal(1, _git.MaxActive);
    }

    [Fact]
    public async Task Forged_worktree_is_refused_for_commit_and_push()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();
        var elsewhere = new TaskWorktree("repo-a", "task-1", "agent/task-1", Path.Combine(_root, "elsewhere"));
        var wrongBranch = new TaskWorktree("repo-a", "task-1", "agent/other", Path.Combine(_root, "worktrees", "repo-a", "task-1"));

        await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.CommitAsync(elsewhere, Request(), ct));
        await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.PushAsync(elsewhere, _ => Task.FromResult("token"), ct));
        await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.CommitAsync(wrongBranch, Request(), ct));

        Assert.Equal(0, _git.CommitCount);
    }

    [Fact]
    public async Task Push_to_untrusted_origin_is_refused_before_credential_is_requested()
    {
        const string secret = "ghs_SECRET_do_not_leak_0123";
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();
        var worktreePath = Path.Combine(_root, "worktrees", "repo-a", "task-1");

        foreach (var origin in new[] { "https://evil.example/owner/repo.git", "http://github.com/owner/repo.git" })
        {
            if (Directory.Exists(worktreePath))
            {
                Directory.Delete(worktreePath, recursive: true);
            }

            InitRepo(worktreePath, origin);
            var credentialRequested = false;

            var error = await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.PushAsync(
                Worktree("repo-a", "task-1"),
                _ =>
                {
                    credentialRequested = true;
                    return Task.FromResult(secret);
                },
                ct));

            Assert.False(credentialRequested);
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void Credential_handler_yields_the_token_once_per_push()
    {
        const string secret = "ghs_ONCE_secret";
        var handler = GitWorkspace.CreateOneShotCredentials(secret);

        var first = Assert.IsType<UsernamePasswordCredentials>(
            handler("https://github.com/owner/repo.git", null, SupportedCredentialTypes.UsernamePassword));
        Assert.Equal(secret, first.Password);

        var second = Assert.Throws<GitWorkspaceException>(
            () => handler("https://github.com/owner/repo.git", null, SupportedCredentialTypes.UsernamePassword));
        Assert.DoesNotContain(secret, second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Credential_handler_refuses_remotes_without_username_and_password()
    {
        var handler = GitWorkspace.CreateOneShotCredentials("ghs_unused");

        Assert.Throws<GitWorkspaceException>(
            () => handler("https://github.com/owner/repo.git", null, (SupportedCredentialTypes)0));
    }

    [Fact]
    public async Task Commit_writes_a_real_commit_on_the_task_branch()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Path.Combine(_root, "work");
        InitRepo(work, null);

        var workspace = CreateWorkspace(new LibGit2GitProvider());
        var worktree = await workspace.CreateTaskWorktreeAsync("repo-a", work, "task-1", ct);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "notes.md"), "Remember the GC.\n", ct);

        var sha = await workspace.CommitAsync(worktree, Request("Add notes"), ct);

        using var repo = new Repository(work);
        var tip = repo.Branches["agent/task-1"].Tip;
        Assert.Equal(sha, tip.Sha);
        Assert.Equal(_bot.Name, tip.Author.Name);
        Assert.Contains("Bazaar-Task: task-1", tip.Message, StringComparison.Ordinal);
        Assert.Contains("Co-Authored-By: Claude Haiku 5.5", tip.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Signed-off-by", tip.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_unsafe_task_and_repo_identifiers()
    {
        var workspace = CreateWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CreateTaskWorktreeAsync(
            "repo-a", Path.Combine(_root, "repo-a"), "../escape", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CreateTaskWorktreeAsync(
            "repo/a", Path.Combine(_root, "repo-a"), "task-1", TestContext.Current.CancellationToken));
    }
}
