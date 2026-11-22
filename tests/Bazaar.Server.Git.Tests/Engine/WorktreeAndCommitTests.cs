using LibGit2Sharp;

namespace Bazaar.Server.Git.Tests.Engine;

public class WorktreeAndCommitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddWorktree_creates_a_branch_worktree_under_the_root()
    {
        using var fixture = new EngineFixture();

        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/feature-1"), Ct);

        Assert.StartsWith(Path.GetFullPath(fixture.WorktreeRoot), handle.Path);
        Assert.True(File.Exists(Path.Combine(handle.Path, "README.md")));
        using var worktree = new Repository(handle.Path);
        Assert.Equal("agent/feature-1", worktree.Head.FriendlyName);
        using var clone = new Repository(fixture.CloneRoot);
        Assert.NotNull(clone.Branches["agent/feature-1"]);
    }

    [Fact]
    public async Task AddWorktree_refuses_an_existing_branch()
    {
        using var fixture = new EngineFixture();
        await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/dup"), Ct);

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/dup"), Ct));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("agent/../../escape")]
    [InlineData("a..b")]
    [InlineData("/abs/path")]
    [InlineData("\\abs")]
    [InlineData("-leading-dash")]
    [InlineData("--upload-pack=x")]
    [InlineData("has\u0001control")]
    [InlineData("has\nnewline")]
    [InlineData("has space")]
    [InlineData("C:\\temp\\x")]
    [InlineData("trailing/")]
    [InlineData("name.lock")]
    [InlineData("")]
    public async Task AddWorktree_refuses_unsafe_branch_names(string name)
    {
        using var fixture = new EngineFixture();

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName(name), Ct));
        Assert.False(Directory.Exists(fixture.WorktreeRoot) && Directory.EnumerateFileSystemEntries(fixture.WorktreeRoot).Any());
    }

    [Fact]
    public async Task AddWorktree_refuses_a_repo_name_that_escapes_the_root()
    {
        using var fixture = new EngineFixture();

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.AddWorktreeAsync(new RepoRef("github", "../../evil"), new BranchName("agent/x"), Ct));
    }

    [Fact]
    public async Task Commit_stages_only_the_listed_paths_and_uses_the_bot_identity()
    {
        using var fixture = new EngineFixture();
        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/commit"), Ct);
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "wanted.txt"), "yes", Ct);
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "unwanted.txt"), "no", Ct);
        Directory.CreateDirectory(Path.Combine(handle.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "src", "a.cs"), "class A;", Ct);

        const string message = "feat: add things\n\nCo-Authored-By: Agent Alice <alice@agents.invalid>\nClaude-Session: https://example.invalid/s/1";
        var id = await fixture.Provider.CommitAsync(handle, new CommitRequest(message, ["wanted.txt", "src/a.cs"]), Ct);

        using var repo = new Repository(handle.Path);
        var commit = repo.Lookup<Commit>(id.Sha);
        Assert.NotNull(commit);
        Assert.NotNull(commit.Tree["wanted.txt"]);
        Assert.NotNull(commit.Tree["src/a.cs"]);
        Assert.Null(commit.Tree["unwanted.txt"]);
        Assert.Equal("Agent Alice", commit.Author.Name);
        Assert.Equal("Bazaar Host", commit.Committer.Name);
        // libgit2 ends a message with one newline; nothing else is added or changed.
        Assert.Equal(message, commit.Message.TrimEnd('\n'));
        Assert.DoesNotContain("Signed-off-by", commit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_refuses_when_something_else_is_already_staged()
    {
        using var fixture = new EngineFixture();
        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/prestaged"), Ct);
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "other.txt"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "mine.txt"), "y", Ct);
        using (var repo = new Repository(handle.Path))
        {
            Commands.Stage(repo, "other.txt");
        }

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.CommitAsync(handle, new CommitRequest("msg", ["mine.txt"]), Ct));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData("a/.git/hooks/x")]
    [InlineData("*.txt")]
    [InlineData("")]
    public async Task Commit_refuses_paths_that_escape_the_worktree(string path)
    {
        using var fixture = new EngineFixture();
        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/escape"), Ct);

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.CommitAsync(handle, new CommitRequest("msg", [path]), Ct));
    }

    [Fact]
    public async Task Commit_stages_a_deletion_of_a_listed_path()
    {
        using var fixture = new EngineFixture();
        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/delete"), Ct);
        File.Delete(Path.Combine(handle.Path, "README.md"));

        var id = await fixture.Provider.CommitAsync(handle, new CommitRequest("rm readme", ["README.md"]), Ct);

        using var repo = new Repository(handle.Path);
        Assert.Null(repo.Lookup<Commit>(id.Sha)!.Tree["README.md"]);
    }
}
