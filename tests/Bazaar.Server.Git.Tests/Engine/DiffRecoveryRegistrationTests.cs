using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.Tests.Engine;

public class DiffRecoveryRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Signature Sig() => new("T", "t@test.invalid", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Diff_reports_added_modified_removed_and_renamed()
    {
        using var fixture = new EngineFixture();
        string oldSha;
        string newSha;
        using (var repo = new Repository(fixture.CloneRoot))
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.CloneRoot, "gone.txt"), "to be removed\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(fixture.CloneRoot, "old-name.txt"), string.Concat(Enumerable.Repeat("rename me please\n", 20)), Ct);
            Commands.Stage(repo, "*");
            oldSha = repo.Commit("base", Sig(), Sig()).Sha;

            await File.WriteAllTextAsync(Path.Combine(fixture.CloneRoot, "README.md"), "hello, changed\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(fixture.CloneRoot, "added.txt"), "new\n", Ct);
            File.Delete(Path.Combine(fixture.CloneRoot, "gone.txt"));
            File.Move(Path.Combine(fixture.CloneRoot, "old-name.txt"), Path.Combine(fixture.CloneRoot, "new-name.txt"));
            Commands.Stage(repo, "*");
            newSha = repo.Commit("change", Sig(), Sig()).Sha;
        }

        var diff = await fixture.Provider.DiffAsync(EngineFixture.Repo, oldSha, newSha, Ct);

        var byPath = diff.ToDictionary(d => d.Path, d => d.Status);
        Assert.Equal("modified", byPath["README.md"]);
        Assert.Equal("added", byPath["added.txt"]);
        Assert.Equal("removed", byPath["gone.txt"]);
        Assert.Equal("renamed", byPath["new-name.txt"]);
        Assert.Equal(4, diff.Count);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main~1")]
    [InlineData("--upload-pack=x")]
    [InlineData("")]
    public async Task Diff_accepts_only_shas(string rev)
    {
        using var fixture = new EngineFixture();

        await Assert.ThrowsAsync<GitEngineException>(() => fixture.Provider.DiffAsync(EngineFixture.Repo, rev, rev, Ct));
    }

    private static string CreateConflictedMerge(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var sig = Sig();
        var baseBranch = repo.Head.FriendlyName;
        var branch = repo.CreateBranch("topic");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "main side\n");
        Commands.Stage(repo, "README.md");
        repo.Commit("main change", sig, sig);
        Commands.Checkout(repo, branch);
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "topic side\n");
        Commands.Stage(repo, "README.md");
        repo.Commit("topic change", sig, sig);
        Commands.Checkout(repo, repo.Branches[baseBranch]);
        var result = repo.Merge(repo.Branches["topic"], sig);
        Assert.Equal(MergeStatus.Conflicts, result.Status);
        Assert.True(File.Exists(Path.Combine(repo.Info.Path, "MERGE_HEAD")));
        return repo.Info.Path;
    }

    [Fact]
    public async Task Recover_aborts_a_half_finished_merge_in_the_main_clone()
    {
        using var fixture = new EngineFixture();
        var gitDir = CreateConflictedMerge(fixture.CloneRoot);

        await fixture.Provider.RecoverAsync(Ct);

        Assert.False(File.Exists(Path.Combine(gitDir, "MERGE_HEAD")));
        using var repo = new Repository(fixture.CloneRoot);
        Assert.DoesNotContain(repo.RetrieveStatus(), e => e.State != FileStatus.Ignored);
        Assert.Equal("main side", File.ReadAllText(Path.Combine(fixture.CloneRoot, "README.md")).TrimEnd());
    }

    [Fact]
    public async Task Recover_aborts_a_merge_in_a_worktree_found_by_scanning_the_worktree_root()
    {
        using var fixture = new EngineFixture();
        fixture.Options.KnownRepos = () => [];
        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/merging"), Ct);
        var adminDir = CreateConflictedMerge(handle.Path);

        await fixture.Provider.RecoverAsync(Ct);

        Assert.False(File.Exists(Path.Combine(adminDir, "MERGE_HEAD")));
        using var repo = new Repository(handle.Path);
        Assert.DoesNotContain(repo.RetrieveStatus(), e => e.State != FileStatus.Ignored);
    }

    [Fact]
    public async Task Recover_prunes_a_worktree_whose_directory_is_gone_and_keeps_live_ones()
    {
        using var fixture = new EngineFixture();
        var stale = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/stale"), Ct);
        var live = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/live"), Ct);
        Directory.Delete(stale.Path, recursive: true);
        var worktreesDir = Path.Combine(fixture.CloneRoot, ".git", "worktrees");
        Assert.Equal(2, Directory.GetDirectories(worktreesDir).Length);

        await fixture.Provider.RecoverAsync(Ct);

        var remaining = Assert.Single(Directory.GetDirectories(worktreesDir));
        Assert.Equal("wt-agent-live", Path.GetFileName(remaining));
        Assert.True(Directory.Exists(live.Path));
        // The branch itself stays; only the worktree entry goes.
        using var repo = new Repository(fixture.CloneRoot);
        Assert.NotNull(repo.Branches["agent/stale"]);
    }

    [Fact]
    public async Task Recover_on_a_clean_setup_does_nothing_and_tolerates_missing_repos()
    {
        using var fixture = new EngineFixture();
        fixture.Options.RepoRoot = _ => Path.Combine(fixture.Root, "missing");

        await fixture.Provider.RecoverAsync(Ct);
    }

    [Fact]
    public async Task AddBazaarGitEngine_registers_the_provider_and_keeps_an_existing_one()
    {
        using var fixture = new EngineFixture();
        var services = new ServiceCollection();
        services.AddSingleton<IGitCredentialSource>(fixture.Credentials);
        services.AddBazaarGitEngine(o =>
        {
            o.WorktreeRoot = fixture.WorktreeRoot;
            o.RepoRoot = _ => fixture.CloneRoot;
            o.RepoLockTimeout = TimeSpan.FromSeconds(5);
        });
        await using var provider = services.BuildServiceProvider();

        var git = provider.GetRequiredService<IGitProvider>();
        Assert.IsType<LibGit2GitProvider>(git);
        Assert.Same(git, provider.GetRequiredService<IGitProvider>());
        var handle = await git.AddWorktreeAsync(EngineFixture.Repo, new BranchName("agent/di"), Ct);
        Assert.True(Directory.Exists(handle.Path));

        var custom = new ServiceCollection();
        var existing = new LibGit2GitProvider(fixture.Options, fixture.Credentials, fixture.Locks);
        custom.AddSingleton<IGitProvider>(existing);
        custom.AddSingleton<IGitCredentialSource>(fixture.Credentials);
        custom.AddBazaarGitEngine(_ => { });
        await using var customProvider = custom.BuildServiceProvider();
        Assert.Same(existing, customProvider.GetRequiredService<IGitProvider>());
    }
}
