using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Git;

/// <summary>The read calls the Vault indexer uses: files at a commit, file text, and the commit list with touched paths.</summary>
public class LibGit2ReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_blobs_in_nested_folders_at_a_commit_in_ordinal_order()
    {
        using var repo = new TempGitRepository(("b/two.md", "two\n"), ("a.md", "one\n"), ("a/nested/three.md", "three\n"));
        var provider = new LibGit2GitProvider();

        var files = await provider.ListFilesAsync(repo.Checkout, TempGitRepository.HeadOf(repo.Checkout), Ct);

        Assert.Equal([".gitattributes", "a.md", "a/nested/three.md", "b/two.md"], files);
    }

    [Fact]
    public async Task Reads_file_text_at_an_older_commit_unchanged()
    {
        using var repo = new TempGitRepository(("note.md", "first line\nsecond\n"));
        var provider = new LibGit2GitProvider();
        var first = TempGitRepository.HeadOf(repo.Checkout);
        TempGitRepository.WriteFile(repo.Checkout, "note.md", "changed\n");
        TempGitRepository.CommitAll(repo.Checkout, "edit");

        Assert.Equal("first line\nsecond\n", await provider.ReadTextAsync(repo.Checkout, first, "note.md", Ct));
        Assert.Equal("changed\n", await provider.ReadTextAsync(repo.Checkout, TempGitRepository.HeadOf(repo.Checkout), "note.md", Ct));
    }

    [Fact]
    public async Task Lists_commits_oldest_first_with_touched_paths_and_resolves_branches()
    {
        using var repo = new TempGitRepository(("a.md", "a\n"));
        var provider = new LibGit2GitProvider();
        var root = TempGitRepository.HeadOf(repo.Checkout);
        TempGitRepository.WriteFile(repo.Checkout, "b.md", "b\n");
        var second = TempGitRepository.CommitAll(repo.Checkout, "add b");

        Assert.Equal(second, await provider.ResolveCommitAsync(repo.Checkout, "main", Ct));

        var commits = await provider.ListCommitsAsync(repo.Checkout, root, second, Ct);
        var only = Assert.Single(commits);
        Assert.Equal(second, only.Sha);
        Assert.Equal([root], only.Parents);
        Assert.Equal(["b.md"], only.TouchedPaths);
        Assert.Equal("add b", only.Message.TrimEnd());

        var all = await provider.ListCommitsAsync(repo.Checkout, null, second, Ct);
        Assert.Equal(new[] { root, second }, all.Select(commit => commit.Sha));
    }
}
