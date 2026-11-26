namespace Bazaar.Server.CodingGit.Tests;

public sealed class WorktreePlacementTests
{
    [Fact]
    public async Task Worktree_is_created_under_the_work_share_on_an_agent_branch()
    {
        using var fx = new CodingGitFixture();
        await using var wt = await fx.OpenAsync();

        var expected = Path.Combine(WorkSharePaths.Canonicalize(fx.Share), "repo", "fix-login");
        Assert.Equal(expected, wt.Path);
        Assert.Equal("agent/fix-login", wt.Branch.Value);
        Assert.Equal(wt.Branch, fx.Engine.BranchAt(wt.Path));
    }

    [Fact]
    public async Task Share_that_is_the_passport_folder_is_refused()
    {
        using var fx = new CodingGitFixture();
        using var same = new CodingGitFixture(worktreeRoot: fx.Passport, passport: fx.Passport);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => same.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
        Assert.Empty(same.Engine.WorktreePaths);
    }

    [Fact]
    public async Task Share_inside_the_passport_folder_is_refused()
    {
        using var fx = new CodingGitFixture();
        var inside = Directory.CreateDirectory(Path.Combine(fx.Passport, "work")).FullName;
        using var f = new CodingGitFixture(worktreeRoot: inside, passport: fx.Passport);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => f.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
    }

    [Fact]
    public async Task Share_that_contains_the_passport_folder_is_refused()
    {
        using var fx = new CodingGitFixture();
        using var f = new CodingGitFixture(worktreeRoot: fx.Base, passport: fx.Passport);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => f.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
    }

    [Fact]
    public async Task Traversal_back_into_the_passport_folder_is_refused()
    {
        using var fx = new CodingGitFixture();
        var sneaky = Path.Combine(fx.Share, "..", "passport");
        using var f = new CodingGitFixture(worktreeRoot: sneaky, passport: fx.Passport);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => f.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
    }

    [Fact]
    public async Task Symlink_repo_directory_pointing_at_the_passport_folder_is_refused()
    {
        using var fx = new CodingGitFixture();
        if (!TryLink(Path.Combine(fx.Share, "repo"), fx.Passport))
        {
            return; // The host cannot create symlinks (unprivileged Windows); the Linux run covers this.
        }

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
        Assert.Empty(fx.Engine.WorktreePaths);
    }

    [Fact]
    public async Task Symlinked_share_that_resolves_to_the_passport_folder_is_refused()
    {
        using var fx = new CodingGitFixture();
        var alias = Path.Combine(fx.Base, "alias");
        if (!TryLink(alias, fx.Passport))
        {
            return;
        }

        using var f = new CodingGitFixture(worktreeRoot: alias, passport: fx.Passport);
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => f.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
    }

    [Fact]
    public async Task Symlink_that_escapes_the_share_is_refused()
    {
        using var fx = new CodingGitFixture();
        var elsewhere = Directory.CreateDirectory(Path.Combine(fx.Base, "elsewhere")).FullName;
        if (!TryLink(Path.Combine(fx.Share, "repo"), elsewhere))
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.OpenAsync());
        Assert.Equal(CodingGitError.WorktreeOutsideWorkShare, ex.Error);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Hostile_task_slugs_never_reach_the_engine(string slug)
    {
        using var fx = new CodingGitFixture();
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.OpenAsync(slug));
        Assert.Equal(CodingGitError.InvalidBranch, ex.Error);
        Assert.Empty(fx.Engine.WorktreePaths);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("-x")]
    public async Task Hostile_repo_ids_are_refused(string repoId)
    {
        using var fx = new CodingGitFixture();
        var request = CodingGitFixture.Request() with { Repo = new RepoRef(repoId, "main") };
        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Service.CreateWorktreeAsync(request));
        Assert.Equal(CodingGitError.InvalidRequest, ex.Error);
    }

    [Fact]
    public void Canonicalize_resolves_dot_dot_and_trailing_separators()
    {
        using var fx = new CodingGitFixture();
        var a = WorkSharePaths.Canonicalize(Path.Combine(fx.Share, "x", "..", "y") + Path.DirectorySeparatorChar);
        Assert.Equal(Path.Combine(WorkSharePaths.Canonicalize(fx.Share), "y"), a);
        Assert.True(WorkSharePaths.IsEqualOrUnder(a, WorkSharePaths.Canonicalize(fx.Share)));
        Assert.False(WorkSharePaths.IsEqualOrUnder(fx.Share + "x", fx.Share));
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
