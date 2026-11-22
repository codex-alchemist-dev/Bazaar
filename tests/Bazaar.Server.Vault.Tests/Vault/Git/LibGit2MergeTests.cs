using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Git;

/// <summary>Merge outcomes, abort, modify/delete conflicts, merge attributes and startup recovery (spike S23, scenario B).</summary>
public sealed class LibGit2MergeTests : IDisposable
{
    private static readonly string _baseDoc = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();

    public LibGit2MergeTests()
    {
        _repo = new TempGitRepository(("doc.md", _baseDoc));
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Disjoint_edits_to_one_file_merge_clean_with_a_merge_commit()
    {
        var feature = _repo.WorktreePath("wt-feature");
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", feature, CancellationToken.None);
        await EditAndCommit(feature, "doc.md", 10, "feature 10");
        await EditAndCommit(_repo.Checkout, "doc.md", 1, "main 1");

        var result = await _git.MergeAsync(_repo.Checkout, "feature", TempGitRepository.Writer, CancellationToken.None);

        Assert.Equal(GitMergeOutcome.Merged, result.Outcome);
        Assert.Empty(result.ConflictPaths);
        Assert.Equal(TempGitRepository.HeadOf(_repo.Checkout), result.CommitSha);
        using (var repo = new Repository(_repo.Checkout))
        {
            Assert.Equal(2, repo.Lookup<Commit>(result.CommitSha!).Parents.Count());
        }

        var merged = TempGitRepository.ReadFile(_repo.Checkout, "doc.md");
        Assert.Contains("main 1", merged);
        Assert.Contains("feature 10", merged);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    [Fact]
    public async Task Merging_a_branch_already_contained_in_HEAD_reports_up_to_date()
    {
        await _git.AddWorktreeAsync(_repo.Checkout, "older", _repo.WorktreePath("wt-older"), CancellationToken.None);
        var head = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _git.MergeAsync(_repo.Checkout, "older", TempGitRepository.Writer, CancellationToken.None);

        Assert.Equal(GitMergeOutcome.UpToDate, result.Outcome);
        Assert.Equal(head, result.CommitSha);
    }

    [Fact]
    public async Task Overlapping_edits_report_the_conflicting_path_and_leave_markers()
    {
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", _repo.WorktreePath("wt-feature"), CancellationToken.None);
        await EditAndCommit(_repo.WorktreePath("wt-feature"), "doc.md", 1, "feature 1");
        await EditAndCommit(_repo.Checkout, "doc.md", 1, "main 1");
        var mainBefore = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _git.MergeAsync(_repo.Checkout, "feature", TempGitRepository.Writer, CancellationToken.None);

        Assert.Equal(GitMergeOutcome.Conflicted, result.Outcome);
        Assert.Null(result.CommitSha);
        Assert.Equal("doc.md", Assert.Single(result.ConflictPaths));
        var text = TempGitRepository.ReadFile(_repo.Checkout, "doc.md");
        Assert.Contains("<<<<<<<", text);
        Assert.Contains(">>>>>>>", text);
        Assert.Contains("main 1", text);
        Assert.Contains("feature 1", text);
        Assert.Equal(mainBefore, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task Abort_restores_a_clean_tree_at_the_old_HEAD_and_keeps_the_branch()
    {
        var feature = _repo.WorktreePath("wt-feature");
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", feature, CancellationToken.None);
        await EditAndCommit(feature, "doc.md", 1, "feature 1");
        TempGitRepository.WriteFile(feature, "added-on-feature.md", "only on feature\n");
        var featureTip = await _git.CommitAsync(feature, "add file on feature", TempGitRepository.Writer, CancellationToken.None);
        await EditAndCommit(_repo.Checkout, "doc.md", 1, "main 1");
        var headBefore = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _git.MergeAsync(_repo.Checkout, "feature", TempGitRepository.Writer, CancellationToken.None);
        Assert.Equal(GitMergeOutcome.Conflicted, result.Outcome);

        await _git.AbortMergeAsync(_repo.Checkout, CancellationToken.None);

        using (var repo = new Repository(_repo.Checkout))
        {
            Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
        }

        Assert.Equal(headBefore, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.False(File.Exists(Path.Combine(_repo.Checkout, "added-on-feature.md")));
        Assert.Equal("main 1", TempGitRepository.ReadFile(_repo.Checkout, "doc.md").Split('\n')[0]);
        Assert.Equal(featureTip, TempGitRepository.TipOf(_repo.Checkout, "feature"));
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    [Fact]
    public async Task Abort_with_no_pending_merge_changes_nothing()
    {
        var head = TempGitRepository.HeadOf(_repo.Checkout);

        await _git.AbortMergeAsync(_repo.Checkout, CancellationToken.None);

        Assert.Equal(head, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task Modify_delete_conflict_reports_the_path_and_abort_restores_it()
    {
        TempGitRepository.WriteFile(_repo.Checkout, "other.md", "shared\n");
        TempGitRepository.CommitAll(_repo.Checkout, "add other.md");
        var feature = _repo.WorktreePath("wt-feature");
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", feature, CancellationToken.None);
        File.Delete(Path.Combine(feature, "other.md"));
        await _git.CommitAsync(feature, "delete other.md", TempGitRepository.Writer, CancellationToken.None);
        await EditAndCommit(_repo.Checkout, "other.md", 1, "shared edited");

        var result = await _git.MergeAsync(_repo.Checkout, "feature", TempGitRepository.Writer, CancellationToken.None);

        Assert.Equal(GitMergeOutcome.Conflicted, result.Outcome);
        Assert.Equal("other.md", Assert.Single(result.ConflictPaths));

        await _git.AbortMergeAsync(_repo.Checkout, CancellationToken.None);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.Contains("shared edited", TempGitRepository.ReadFile(_repo.Checkout, "other.md"));
    }

    [Fact]
    public async Task Markdown_union_attribute_keeps_both_sides_of_an_append_conflict()
    {
        TempGitRepository.WriteFile(_repo.Checkout, ".gitattributes", "*.md merge=union\n");
        TempGitRepository.WriteFile(_repo.Checkout, "log.md", "# Log\n");
        TempGitRepository.CommitAll(_repo.Checkout, "attributes and log");
        var feature = _repo.WorktreePath("wt-feature");
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", feature, CancellationToken.None);
        TempGitRepository.WriteFile(feature, "log.md", "# Log\n- feature entry\n");
        await _git.CommitAsync(feature, "feature entry", TempGitRepository.Writer, CancellationToken.None);
        TempGitRepository.WriteFile(_repo.Checkout, "log.md", "# Log\n- main entry\n");
        await _git.CommitAsync(_repo.Checkout, "main entry", TempGitRepository.Writer, CancellationToken.None);

        var result = await _git.MergeAsync(_repo.Checkout, "feature", TempGitRepository.Writer, CancellationToken.None);

        Assert.Equal(GitMergeOutcome.Merged, result.Outcome);
        var text = TempGitRepository.ReadFile(_repo.Checkout, "log.md");
        Assert.Contains("- main entry", text);
        Assert.Contains("- feature entry", text);
        Assert.DoesNotContain("<<<<<<<", text);
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    [Fact]
    public async Task Recovery_aborts_a_merge_left_pending_by_a_crash()
    {
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", _repo.WorktreePath("wt-feature"), CancellationToken.None);
        await EditAndCommit(_repo.WorktreePath("wt-feature"), "doc.md", 1, "feature 1");
        await EditAndCommit(_repo.Checkout, "doc.md", 1, "main 1");
        var headBefore = TempGitRepository.HeadOf(_repo.Checkout);
        Assert.Equal(GitMergeOutcome.Conflicted, (await _git.MergeAsync(_repo.Checkout, "feature", TempGitRepository.Writer, CancellationToken.None)).Outcome);

        // A new provider instance stands in for the process restarting after the crash.
        var restarted = new LibGit2GitProvider();
        var report = await restarted.RecoverAsync(_repo.Checkout, CancellationToken.None);

        Assert.Equal(_repo.Checkout, Assert.Single(report.AbortedMerges));
        using (var repo = new Repository(_repo.Checkout))
        {
            Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
        }

        Assert.Equal(headBefore, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);

        var second = await restarted.RecoverAsync(_repo.Checkout, CancellationToken.None);
        Assert.Empty(second.AbortedMerges);
        Assert.Empty(second.PrunedWorktrees);
    }

    [Fact]
    public async Task Recovery_aborts_a_merge_left_pending_inside_a_worktree()
    {
        var feature = _repo.WorktreePath("wt-feature");
        var writer = _repo.WorktreePath("wt-writer");
        await _git.AddWorktreeAsync(_repo.Checkout, "feature", feature, CancellationToken.None);
        await _git.AddWorktreeAsync(_repo.Checkout, "writer", writer, CancellationToken.None);
        await EditAndCommit(feature, "doc.md", 1, "feature 1");
        await EditAndCommit(writer, "doc.md", 1, "writer 1");
        Assert.Equal(GitMergeOutcome.Conflicted, (await _git.MergeAsync(writer, "feature", TempGitRepository.Writer, CancellationToken.None)).Outcome);

        var report = await new LibGit2GitProvider().RecoverAsync(_repo.Checkout, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(writer), Assert.Single(report.AbortedMerges));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(writer));
        Assert.Equal("writer 1", TempGitRepository.ReadFile(writer, "doc.md").Split('\n')[0]);
    }

    [Fact]
    public async Task Recovery_prunes_stale_worktrees_and_keeps_their_branches()
    {
        var stale = _repo.WorktreePath("wt-stale");
        await _git.AddWorktreeAsync(_repo.Checkout, "stale", stale, CancellationToken.None);
        await _git.AddWorktreeAsync(_repo.Checkout, "live", _repo.WorktreePath("wt-live"), CancellationToken.None);
        Directory.Delete(stale, recursive: true);

        var report = await _git.RecoverAsync(_repo.Checkout, CancellationToken.None);

        Assert.Equal("wt-stale", Assert.Single(report.PrunedWorktrees));
        Assert.Empty(report.AbortedMerges);
        var remaining = await _git.ListWorktreesAsync(_repo.Checkout, CancellationToken.None);
        Assert.Equal("wt-live", Assert.Single(remaining).Name);
        Assert.NotNull(TempGitRepository.TipOf(_repo.Checkout, "stale"));
    }

    private async Task EditAndCommit(string workdir, string relativePath, int lineNumber, string newText)
    {
        var lines = (await File.ReadAllLinesAsync(Path.Combine(workdir, relativePath))).ToList();
        lines[lineNumber - 1] = newText;
        await File.WriteAllLinesAsync(Path.Combine(workdir, relativePath), lines);
        await _git.CommitAsync(workdir, $"edit line {lineNumber}", TempGitRepository.Writer, CancellationToken.None);
    }
}
