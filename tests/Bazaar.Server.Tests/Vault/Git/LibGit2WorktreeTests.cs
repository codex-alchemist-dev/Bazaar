using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Git;

/// <summary>Worktree add, list, prune and commit-in-worktree on temporary repositories (spike S23, scenario A1 and A2).</summary>
public sealed class LibGit2WorktreeTests : IDisposable
{
    private readonly TempGitRepository _repo = new(("doc.md", "line 1\nline 2\n"));
    private readonly LibGit2GitProvider _git = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task AddWorktree_creates_a_new_branch_from_HEAD()
    {
        var path = _repo.WorktreePath("wt-feature");

        var created = await _git.AddWorktreeAsync(_repo.Checkout, "feature", path, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(path), created);
        Assert.True(File.Exists(Path.Combine(path, "doc.md")));
        Assert.Equal(TempGitRepository.HeadOf(_repo.Checkout), TempGitRepository.TipOf(_repo.Checkout, "feature"));
    }

    [Fact]
    public async Task AddWorktree_on_an_existing_branch_checks_that_branch_out()
    {
        using (var repo = new Repository(_repo.Checkout))
        {
            repo.CreateBranch("existing");
        }

        var path = _repo.WorktreePath("wt-existing");
        await _git.AddWorktreeAsync(_repo.Checkout, "existing", path, CancellationToken.None);

        var worktrees = await _git.ListWorktreesAsync(_repo.Checkout, CancellationToken.None);
        var entry = Assert.Single(worktrees);
        Assert.Equal("existing", entry.Branch);
        Assert.False(entry.IsStale);
    }

    [Fact]
    public async Task ListWorktrees_reports_name_path_and_branch_for_each_worktree()
    {
        await _git.AddWorktreeAsync(_repo.Checkout, "alpha", _repo.WorktreePath("wt-alpha"), CancellationToken.None);
        await _git.AddWorktreeAsync(_repo.Checkout, "beta", _repo.WorktreePath("wt-beta"), CancellationToken.None);

        var worktrees = await _git.ListWorktreesAsync(_repo.Checkout, CancellationToken.None);

        Assert.Equal(2, worktrees.Count);
        var alpha = Assert.Single(worktrees, w => w.Name == "wt-alpha");
        Assert.Equal(Path.GetFullPath(_repo.WorktreePath("wt-alpha")), alpha.Path);
        Assert.Equal("alpha", alpha.Branch);
        Assert.False(alpha.IsLocked);
        Assert.Equal("beta", Assert.Single(worktrees, w => w.Name == "wt-beta").Branch);
    }

    [Fact]
    public async Task PruneWorktrees_removes_only_worktrees_whose_directory_is_gone()
    {
        var stalePath = _repo.WorktreePath("wt-stale");
        var livePath = _repo.WorktreePath("wt-live");
        await _git.AddWorktreeAsync(_repo.Checkout, "stale", stalePath, CancellationToken.None);
        await _git.AddWorktreeAsync(_repo.Checkout, "live", livePath, CancellationToken.None);
        Directory.Delete(stalePath, recursive: true);

        var pruned = await _git.PruneWorktreesAsync(_repo.Checkout, CancellationToken.None);

        Assert.Equal("wt-stale", Assert.Single(pruned));
        var remaining = await _git.ListWorktreesAsync(_repo.Checkout, CancellationToken.None);
        var live = Assert.Single(remaining);
        Assert.Equal("wt-live", live.Name);
        Assert.True(Directory.Exists(livePath));
        Assert.NotNull(TempGitRepository.TipOf(_repo.Checkout, "stale"));
    }

    [Fact]
    public async Task CommitAsync_in_a_worktree_advances_only_that_branch()
    {
        var path = _repo.WorktreePath("wt-feature");
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", path, CancellationToken.None);
        var mainBefore = TempGitRepository.TipOf(_repo.Checkout, "main");

        TempGitRepository.WriteFile(path, "notes/new.md", "fresh note\n");
        var sha = await _git.CommitAsync(path, "add note", TempGitRepository.Writer, CancellationToken.None);

        Assert.Equal(sha, TempGitRepository.TipOf(_repo.Checkout, "feature"));
        Assert.Equal(mainBefore, TempGitRepository.TipOf(_repo.Checkout, "main"));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(path));
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    [Fact]
    public async Task CommitAsync_stages_deletions_as_well_as_edits_and_additions()
    {
        TempGitRepository.WriteFile(_repo.Checkout, "gone.md", "to be removed\n");
        TempGitRepository.CommitAll(_repo.Checkout, "add gone.md");
        var path = _repo.WorktreePath("wt-delete");
        await _git.AddWorktreeAsync(_repo.Checkout, "delete-side", path, CancellationToken.None);

        File.Delete(Path.Combine(path, "gone.md"));
        File.AppendAllText(Path.Combine(path, "doc.md"), "line 3\n");
        var sha = await _git.CommitAsync(path, "delete and edit", TempGitRepository.Writer, CancellationToken.None);

        var changes = await _git.DiffAsync(_repo.Checkout, TempGitRepository.TipOf(_repo.Checkout, "main"), sha, CancellationToken.None);
        Assert.Contains(changes, c => c.Path == "gone.md" && c.Kind == DiffChangeKind.Deleted);
        Assert.Contains(changes, c => c.Path == "doc.md" && c.Kind == DiffChangeKind.Modified);
    }

    [Fact]
    public async Task Diff_reports_added_modified_and_deleted_paths()
    {
        TempGitRepository.WriteFile(_repo.Checkout, "old.md", "remove me\n");
        var oldSha = TempGitRepository.CommitAll(_repo.Checkout, "add old.md");
        TempGitRepository.WriteFile(_repo.Checkout, "doc.md", "changed\n");
        File.Delete(Path.Combine(_repo.Checkout, "old.md"));
        TempGitRepository.WriteFile(_repo.Checkout, "added.md", "new\n");
        var newSha = TempGitRepository.CommitAll(_repo.Checkout, "second");

        var changes = await _git.DiffAsync(_repo.Checkout, oldSha, newSha, CancellationToken.None);

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Path == "added.md" && c.Kind == DiffChangeKind.Added);
        Assert.Contains(changes, c => c.Path == "doc.md" && c.Kind == DiffChangeKind.Modified);
        Assert.Contains(changes, c => c.Path == "old.md" && c.Kind == DiffChangeKind.Deleted);
    }
}
