using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Git;

/// <summary>The Time Machine calls: nearest commit at or before a time, and a branch created from a commit.</summary>
public class LibGit2TimeTravelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Far in the past, so the fixture's own "initial" commit (stamped now) is newer than every commit below.
    private static readonly DateTimeOffset _t0 = new(2001, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Commits with a fixed committer time so the tests do not depend on the wall clock.</summary>
    private static string CommitAt(string workdir, string file, DateTimeOffset when)
    {
        TempGitRepository.WriteFile(workdir, file, when.ToString("O"));
        using var repo = new Repository(workdir);
        Commands.Stage(repo, "*");
        var signature = new Signature(TempGitRepository.Writer.Name, TempGitRepository.Writer.Email, when);
        return repo.Commit("at " + when.ToString("O"), signature, signature).Sha;
    }

    /// <summary>A repository whose three commits sit at _t0, _t0+1h and _t0+2h on top of the fixture's initial commit.</summary>
    private static (TempGitRepository Repo, string A, string B, string C) ThreeCommits()
    {
        var repo = new TempGitRepository();
        var a = CommitAt(repo.Checkout, "a.md", _t0);
        var b = CommitAt(repo.Checkout, "b.md", _t0.AddHours(1));
        var c = CommitAt(repo.Checkout, "c.md", _t0.AddHours(2));
        return (repo, a, b, c);
    }

    [Fact]
    public async Task Returns_null_for_a_repository_without_commits()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-tt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Repository.Init(path);
            Assert.Null(await new LibGit2VaultTimeTravel().NearestCommitAtOrBeforeAsync(path, _t0, Ct));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task Returns_null_before_the_first_commit()
    {
        using var repo = new TempGitRepository();
        Assert.Null(await new LibGit2VaultTimeTravel().NearestCommitAtOrBeforeAsync(repo.Checkout, _t0, Ct));
    }

    [Fact]
    public async Task Returns_the_commit_at_the_exact_time()
    {
        var (repo, _, b, _) = ThreeCommits();
        using (repo)
        {
            Assert.Equal(b, await new LibGit2VaultTimeTravel().NearestCommitAtOrBeforeAsync(repo.Checkout, _t0.AddHours(1), Ct));
        }
    }

    [Fact]
    public async Task Returns_the_earlier_commit_between_two_commits()
    {
        var (repo, a, b, _) = ThreeCommits();
        using (repo)
        {
            var provider = new LibGit2VaultTimeTravel();
            Assert.Equal(a, await provider.NearestCommitAtOrBeforeAsync(repo.Checkout, _t0.AddMinutes(30), Ct));
            Assert.Equal(b, await provider.NearestCommitAtOrBeforeAsync(repo.Checkout, _t0.AddMinutes(119), Ct));
        }
    }

    [Fact]
    public async Task Returns_the_last_commit_after_the_last_commit()
    {
        var (repo, _, _, c) = ThreeCommits();
        using (repo)
        {
            Assert.Equal(c, await new LibGit2VaultTimeTravel().NearestCommitAtOrBeforeAsync(repo.Checkout, _t0.AddYears(5), Ct));
        }
    }

    [Fact]
    public async Task Creates_a_branch_at_the_commit_without_touching_the_main_checkout()
    {
        var (repo, a, _, c) = ThreeCommits();
        using (repo)
        {
            await new LibGit2VaultTimeTravel().CreateBranchFromCommitAsync(repo.Checkout, "fork/gc-1", a, Ct);

            Assert.Equal(a, TempGitRepository.TipOf(repo.Checkout, "fork/gc-1"));
            Assert.Equal(c, TempGitRepository.HeadOf(repo.Checkout));
            Assert.Equal(0, TempGitRepository.DirtyEntryCount(repo.Checkout));
            using var open = new Repository(repo.Checkout);
            Assert.Equal("main", open.Head.FriendlyName);
            Assert.True(File.Exists(Path.Combine(repo.Checkout, "c.md")));
            Assert.Empty(open.Worktrees);
        }
    }

    [Fact]
    public async Task Refuses_an_existing_branch_and_leaves_it_alone()
    {
        var (repo, a, _, c) = ThreeCommits();
        using (repo)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LibGit2VaultTimeTravel().CreateBranchFromCommitAsync(repo.Checkout, "main", a, Ct));
            Assert.Equal(c, TempGitRepository.TipOf(repo.Checkout, "main"));
        }
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("not-a-sha")]
    public async Task Refuses_a_commit_that_is_not_in_the_repository(string sha)
    {
        var (repo, _, _, _) = ThreeCommits();
        using (repo)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new LibGit2VaultTimeTravel().CreateBranchFromCommitAsync(repo.Checkout, "fork/x", sha, Ct));
            using var open = new Repository(repo.Checkout);
            Assert.Null(open.Branches["fork/x"]);
        }
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("a..b")]
    [InlineData("-lead")]
    public async Task Refuses_an_invalid_branch_name(string branch)
    {
        var (repo, a, _, _) = ThreeCommits();
        using (repo)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new LibGit2VaultTimeTravel().CreateBranchFromCommitAsync(repo.Checkout, branch, a, Ct));
        }
    }
}
