using Bazaar.GitHub.Workspace;
using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.GitHub.Tests.Workspace;

public sealed class GitWorkspaceDiffTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bazaar-diff-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Roots(string root) : IWorkShareRootResolver
    {
        public string? GetRoot(string agentId) => agentId == "agent-1" ? root : null;
    }

    private static string InitWorktree(string path)
    {
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        Commands.Stage(repo, "a.txt");
        var sig = new Signature("bot", "bot@example.invalid", DateTimeOffset.UtcNow);
        repo.Commit("base", sig, sig);
        return repo.Head.Tip.Sha;
    }

    [Fact]
    public async Task Diff_returns_per_file_patches_against_the_base_ref()
    {
        var path = Path.Combine(_root, "wt", "repo-a", "task-1");
        var baseSha = InitWorktree(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(path, "new.txt"), "fresh\n");
        var workspace = new GitWorkspace(new FakeGitProvider(), new GitWorkspaceOptions { RootPath = Path.Combine(_root, "wt") });

        var diff = await workspace.DiffAsync(
            new TaskWorktree("repo-a", "task-1", "agent/task-1", path), baseSha, TestContext.Current.CancellationToken);

        Assert.Equal(["a.txt", "new.txt"], diff.Select(d => d.Path));
        Assert.Equal(DiffChangeKind.Modified, diff[0].Kind);
        Assert.Contains("+two", diff[0].Patch, StringComparison.Ordinal);
        Assert.Equal(DiffChangeKind.Added, diff[1].Kind);
        Assert.Contains("+fresh", diff[1].Patch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_of_an_unknown_base_ref_fails()
    {
        var path = Path.Combine(_root, "wt", "repo-a", "task-1");
        InitWorktree(path);
        var workspace = new GitWorkspace(new FakeGitProvider(), new GitWorkspaceOptions { RootPath = Path.Combine(_root, "wt") });

        await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.DiffAsync(
            new TaskWorktree("repo-a", "task-1", "agent/task-1", path), "no-such-ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Diff_refuses_a_worktree_outside_the_workspace()
    {
        var workspace = new GitWorkspace(new FakeGitProvider(), new GitWorkspaceOptions { RootPath = Path.Combine(_root, "wt") });

        await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.DiffAsync(
            new TaskWorktree("repo-a", "task-1", "agent/task-1", Path.Combine(_root, "elsewhere")), "main", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_per_agent_root_places_and_validates_the_worktree_there()
    {
        var agentRoot = Path.Combine(_root, "agent-share");
        var workspace = new GitWorkspace(new FakeGitProvider(), new GitWorkspaceOptions { RootPath = Path.Combine(_root, "wt") }, new Roots(agentRoot));

        var created = await workspace.CreateTaskWorktreeAsync("agent-1", "repo-a", Path.Combine(_root, "repo-a"), "task-1", TestContext.Current.CancellationToken);
        var other = await workspace.CreateTaskWorktreeAsync("agent-2", "repo-a", Path.Combine(_root, "repo-a"), "task-2", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(agentRoot, "repo-a", "task-1"), created.Path);
        Assert.Equal("agent-1", created.AgentId);
        Assert.Equal(Path.Combine(_root, "wt", "repo-a", "task-2"), other.Path);

        // A worktree claiming agent-1 but sitting under the global root is not valid for that agent.
        var wrong = created with { Path = Path.Combine(_root, "wt", "repo-a", "task-1") };
        await Assert.ThrowsAsync<GitWorkspaceException>(() => workspace.DiffAsync(wrong, "main", TestContext.Current.CancellationToken));
    }
}
