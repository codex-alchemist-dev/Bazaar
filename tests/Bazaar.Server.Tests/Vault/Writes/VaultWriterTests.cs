using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>Vault write path: merge with trailers, conflicts, queues and decision numbering (doc 18, write path).</summary>
public sealed class VaultWriterTests : IDisposable
{
    private static readonly string _baseDoc = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";
    private static readonly Guid _docId = Guid.Parse("0196a7c4-0000-7000-8000-000000000001");

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;

    public VaultWriterTests()
    {
        _repo = new TempGitRepository(("doc.md", _baseDoc), ("other.md", _baseDoc));
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Submitting_a_branch_merges_it_and_records_trailers()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewAgentBranchAsync("task-1");
        await EditAndCommit(_repo.WorktreePath("task-1"), "doc.md", 10, "agent 10");
        await EditAndCommit(_repo.Checkout, "other.md", 1, "main 1");
        var before = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.SubmitAsync(
            new VaultEditRequest(_docId, baseCommit, "agent/task-1", "agent-ana", "gc-7", "task-1"),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
        Assert.Equal(before, result.OldCommit);
        Assert.Equal(TempGitRepository.HeadOf(_repo.Checkout), result.NewCommit);
        Assert.Empty(result.ConflictPaths);
        Assert.Null(result.DecisionNumber);

        using var repo = new Repository(_repo.Checkout);
        var commit = repo.Lookup<Commit>(result.NewCommit);
        Assert.Equal(2, commit.Parents.Count());
        Assert.Contains("Bazaar-Agent: agent-ana", commit.Message);
        Assert.Contains("Bazaar-GC: gc-7", commit.Message);
        Assert.Contains("Bazaar-Task: task-1", commit.Message);
        Assert.Contains($"Bazaar-Edit-Base: {baseCommit}", commit.Message);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task Memory_writes_without_gc_or_task_omit_those_trailers()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewAgentBranchAsync("memory-1");
        await EditAndCommit(_repo.WorktreePath("memory-1"), "doc.md", 3, "memory note");

        var result = await _writer.SubmitAsync(
            new VaultEditRequest(_docId, baseCommit, "agent/memory-1", "agent-ana", null, null),
            CancellationToken.None);

        using var repo = new Repository(_repo.Checkout);
        var message = repo.Lookup<Commit>(result.NewCommit).Message;
        Assert.Contains("Bazaar-Agent: agent-ana", message);
        Assert.DoesNotContain("Bazaar-GC:", message);
        Assert.DoesNotContain("Bazaar-Task:", message);
    }

    [Fact]
    public async Task Submitting_a_branch_already_in_main_reports_up_to_date()
    {
        await NewAgentBranchAsync("older");
        var head = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.SubmitAsync(
            new VaultEditRequest(_docId, head, "agent/older", "agent-ana", null, null),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.UpToDate, result.Outcome);
        Assert.Equal(head, result.NewCommit);
    }

    [Fact]
    public async Task Overlapping_edits_are_conflicted_and_leave_main_clean_with_the_branch_kept()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewAgentBranchAsync("clash");
        await EditAndCommit(_repo.WorktreePath("clash"), "doc.md", 5, "agent 5");
        await EditAndCommit(_repo.Checkout, "doc.md", 5, "main 5");
        var mainHead = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.SubmitAsync(
            new VaultEditRequest(_docId, baseCommit, "agent/clash", "agent-ana", null, null),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Conflicted, result.Outcome);
        Assert.Equal(["doc.md"], result.ConflictPaths);
        Assert.Equal(mainHead, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.NotEqual(mainHead, TempGitRepository.TipOf(_repo.Checkout, "agent/clash"));
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    [Fact]
    public async Task Concurrent_submissions_all_land_one_commit_at_a_time()
    {
        const int count = 8;
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        var docs = Enumerable.Range(1, count).Select(i => $"notes/n{i}.md").ToArray();
        for (var i = 0; i < count; i++)
        {
            await NewAgentBranchAsync($"c{i}");
            var worktree = _repo.WorktreePath($"c{i}");
            TempGitRepository.WriteFile(worktree, docs[i], $"note {i}\n");
            TempGitRepository.CommitAll(worktree, $"note {i}");
        }

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            _writer.SubmitAsync(
                new VaultEditRequest(Guid.NewGuid(), baseCommit, $"agent/c{i}", "agent-ana", null, null),
                CancellationToken.None)));

        Assert.All(results, r => Assert.Equal(VaultEditOutcome.Merged, r.Outcome));
        for (var i = 0; i < count; i++)
        {
            Assert.Equal($"note {i}\n", TempGitRepository.ReadFile(_repo.Checkout, docs[i]).Replace("\r\n", "\n"));
        }

        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task Decisions_are_numbered_in_order_and_committed()
    {
        var first = await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "use-postgres", "Use PostgreSQL", "We keep Postgres.", "agent-ana"),
            CancellationToken.None);
        var second = await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "vault-in-git", "Vault in git", "Markdown in git.", "agent-ana"),
            CancellationToken.None);

        Assert.Equal(1, first.DecisionNumber);
        Assert.Equal(2, second.DecisionNumber);
        var text = TempGitRepository.ReadFile(_repo.Checkout, "decisions/0001-use-postgres.md");
        Assert.StartsWith("---\nid: ", text);
        Assert.Contains("type: decision\n", text);
        Assert.Contains("status: proposed\n", text);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.Equal(second.NewCommit, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Theory]
    [InlineData("Bad_Slug")]
    [InlineData("double--hyphen")]
    [InlineData("")]
    public async Task Decision_slugs_must_be_lowercase_words(string slug)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), slug, "Title", "Body", "agent-ana"),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("Auth: token flow")]
    [InlineData("Uses [[links]]")]
    [InlineData("Heading # one")]
    public async Task Decision_titles_reject_characters_front_matter_forbids(string title)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "slug-a", title, "Body", "agent-ana"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Trailer_values_must_be_single_line()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _writer.SubmitAsync(
            new VaultEditRequest(_docId, "abc", "agent/x", "agent\nBazaar-GC: forged", null, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task Decision_numbers_continue_after_the_highest_existing_prefix()
    {
        TempGitRepository.WriteFile(_repo.Checkout, "decisions/0003-later.md", "x");
        TempGitRepository.WriteFile(_repo.Checkout, "decisions/README.md", "x");
        TempGitRepository.CommitAll(_repo.Checkout, "existing decisions");

        var result = await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "next-one", "Next one", "Body.", "agent-ana"),
            CancellationToken.None);

        Assert.Equal(4, result.DecisionNumber);
        Assert.True(File.Exists(Path.Combine(_repo.Checkout, "decisions", "0004-next-one.md")));
    }

    private async Task NewAgentBranchAsync(string task) =>
        await _git.AddWorktreeAsync(_repo.Checkout, "agent/" + task, _repo.WorktreePath(task), CancellationToken.None);

    private static async Task EditAndCommit(string workdir, string file, int line, string text)
    {
        var lines = File.ReadAllLines(Path.Combine(workdir, file));
        lines[line - 1] = text;
        await File.WriteAllTextAsync(Path.Combine(workdir, file), string.Join('\n', lines) + "\n");
        TempGitRepository.CommitAll(workdir, $"edit {file} line {line}");
    }
}
