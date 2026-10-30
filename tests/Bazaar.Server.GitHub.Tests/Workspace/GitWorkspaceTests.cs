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

    private static CommitRequest Request(string message = "Update notes") =>
        new(message, _bot, "agent-1", "gc-1", "task-1", "Claude Haiku 5.5 <noreply@anthropic.com>");

    [Fact]
    public async Task Refuses_default_branch_before_asking_for_credential()
    {
        var workspace = CreateWorkspace();
        var credentialRequested = false;
        var worktree = new TaskWorktree("repo-a", "task-1", "main", Path.Combine(_root, "main-checkout"));

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
            var worktree = new TaskWorktree("repo-a", "task-1", branch, Path.Combine(_root, "checkout"));
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
        var worktree = new TaskWorktree("repo-a", "task-1", "agent/task-1", Path.Combine(_root, "checkout"));

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.CommitAsync(
            worktree,
            Request("   "),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _git.CommitCount);
    }

    [Fact]
    public async Task Refuses_signed_off_by_in_commit_message()
    {
        var workspace = CreateWorkspace();
        var worktree = new TaskWorktree("repo-a", "task-1", "agent/task-1", Path.Combine(_root, "checkout"));

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
        var worktree = new TaskWorktree("repo-a", "task-1", "agent/task-1", Path.Combine(_root, "checkout"));

        var sha = await workspace.CommitAsync(worktree, Request(), TestContext.Current.CancellationToken);

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
        var workspace = CreateWorkspace();
        var first = new TaskWorktree("repo-a", "task-1", "agent/task-1", Path.Combine(_root, "a"));
        var second = new TaskWorktree("repo-a", "task-2", "agent/task-2", Path.Combine(_root, "b"));

        await Task.WhenAll(
            workspace.CommitAsync(first, Request("one"), TestContext.Current.CancellationToken),
            workspace.CommitAsync(second, Request("two"), TestContext.Current.CancellationToken),
            workspace.CreateTaskWorktreeAsync("repo-a", Path.Combine(_root, "repo-a"), "task-3", TestContext.Current.CancellationToken),
            workspace.CommitAsync(first, Request("three"), TestContext.Current.CancellationToken));

        Assert.Equal(3, _git.CommitCount);
        Assert.Equal(1, _git.MaxActive);
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

    [Fact]
    public async Task Failed_push_does_not_leak_the_credential()
    {
        const string secret = "ghs_SECRET_do_not_leak_0123";
        var missingRemote = Path.Combine(_root, "missing-remote.git");
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(checkout);
        Repository.Init(checkout);
        using (var repo = new Repository(checkout))
        {
            var signature = new Signature(_bot.Name, _bot.Email, DateTimeOffset.UtcNow);
            var commit = repo.Commit("Initial commit", signature, signature, new CommitOptions { AllowEmptyCommit = true });
            repo.CreateBranch("agent/task-1", commit);
            repo.Network.Remotes.Add("origin", missingRemote);
        }

        var workspace = CreateWorkspace();
        var worktree = new TaskWorktree("repo-a", "task-1", "agent/task-1", checkout);

        var error = await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.PushAsync(
            worktree,
            _ => Task.FromResult(secret),
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Commit_and_push_reach_a_local_bare_remote()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Path.Combine(_root, "work");
        var bare = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(work);
        Repository.Init(bare, isBare: true);

        Repository.Init(work);
        using (var repo = new Repository(work))
        {
            await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "# Notes\n", ct);
            Commands.Stage(repo, "README.md");
            var signature = new Signature(_bot.Name, _bot.Email, DateTimeOffset.UtcNow);
            repo.Commit("Initial commit", signature, signature);
            repo.Network.Remotes.Add("origin", bare);
        }

        var workspace = CreateWorkspace(new LibGit2GitProvider());
        var worktree = await workspace.CreateTaskWorktreeAsync("repo-a", work, "task-1", ct);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "notes.md"), "Remember the GC.\n", ct);

        var sha = await workspace.CommitAsync(worktree, Request("Add notes"), ct);
        await workspace.PushAsync(worktree, _ => Task.FromResult("ghs_test_token"), ct);

        using var remote = new Repository(bare);
        var tip = remote.Branches["agent/task-1"].Tip;
        Assert.Equal(sha, tip.Sha);
        Assert.Equal(_bot.Name, tip.Author.Name);
        Assert.Contains("Bazaar-Task: task-1", tip.Message, StringComparison.Ordinal);
        Assert.Contains("Co-Authored-By: Claude Haiku 5.5", tip.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Signed-off-by", tip.Message, StringComparison.OrdinalIgnoreCase);
    }
}
