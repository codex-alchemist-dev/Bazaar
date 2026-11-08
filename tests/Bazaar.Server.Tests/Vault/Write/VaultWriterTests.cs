using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Write;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Write;

/// <summary>The write path on real git repositories (doc 18, "Write path"). No database is involved.</summary>
public class VaultWriterTests
{
    private const string _base = "# Top\n\nTop paragraph.\n\n# Bottom\n\nBottom paragraph.\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultWriter Writer() => new(new LibGit2GitProvider());

    private static VaultEditRequest Edit(string repo, string branch, string path, string content) =>
        new(repo, branch, path, null, content, "Agent One", "agent-one@example.invalid", TempGitRepository.HeadOf(repo), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task A_new_document_lands_on_main_and_the_agent_commit_carries_trailers()
    {
        using var repo = new TempGitRepository(("index.md", "# Index\n"));
        var gc = Guid.NewGuid();
        var task = Guid.NewGuid();
        var baseCommit = TempGitRepository.HeadOf(repo.Checkout);
        var request = new VaultEditRequest(repo.Checkout, "agent/one", "notes/one.md", null, "# One\n", "Agent One", "agent-one@example.invalid", baseCommit, gc, task);

        var result = await Writer().SubmitAsync(request, Ct);

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
        Assert.Equal("notes/one.md", result.Path);
        Assert.Equal("# One\n", TempGitRepository.ReadFile(repo.Checkout, "notes/one.md"));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(repo.Checkout));

        using var git = new Repository(repo.Checkout);
        var message = git.Branches["agent/one"].Tip.Message;
        Assert.Contains($"Bazaar-Agent: Agent One", message, StringComparison.Ordinal);
        Assert.Contains($"Bazaar-GC: {gc}", message, StringComparison.Ordinal);
        Assert.Contains($"Bazaar-Task: {task}", message, StringComparison.Ordinal);
        Assert.Contains($"Bazaar-Edit-Base: {baseCommit}", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edits_to_different_sections_of_one_file_both_merge()
    {
        using var repo = new TempGitRepository(("doc.md", _base));
        using (var git = new Repository(repo.Checkout))
        {
            git.CreateBranch("agent/top");
            git.CreateBranch("agent/bottom");
        }

        var writer = Writer();
        var top = await writer.SubmitAsync(Edit(repo.Checkout, "agent/top", "doc.md", _base.Replace("Top paragraph.", "Top paragraph, edited.")), Ct);
        var bottom = await writer.SubmitAsync(Edit(repo.Checkout, "agent/bottom", "doc.md", _base.Replace("Bottom paragraph.", "Bottom paragraph, edited.")), Ct);

        Assert.Equal(VaultEditOutcome.Merged, top.Outcome);
        Assert.Equal(VaultEditOutcome.Merged, bottom.Outcome);
        var merged = TempGitRepository.ReadFile(repo.Checkout, "doc.md");
        Assert.Contains("Top paragraph, edited.", merged, StringComparison.Ordinal);
        Assert.Contains("Bottom paragraph, edited.", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_edits_conflict_leave_main_untouched_and_keep_the_branch()
    {
        using var repo = new TempGitRepository(("doc.md", _base));
        using (var git = new Repository(repo.Checkout))
        {
            git.CreateBranch("agent/first");
            git.CreateBranch("agent/second");
        }

        var writer = Writer();
        var first = await writer.SubmitAsync(Edit(repo.Checkout, "agent/first", "doc.md", _base.Replace("Top paragraph.", "First wins.")), Ct);
        var headAfterFirst = TempGitRepository.HeadOf(repo.Checkout);

        var second = await writer.SubmitAsync(Edit(repo.Checkout, "agent/second", "doc.md", _base.Replace("Top paragraph.", "Second wins.")), Ct);

        Assert.Equal(VaultEditOutcome.Merged, first.Outcome);
        Assert.Equal(VaultEditOutcome.Conflicted, second.Outcome);
        Assert.Contains("doc.md", second.ConflictPaths);
        Assert.Null(second.CommitSha);
        Assert.Equal(headAfterFirst, TempGitRepository.HeadOf(repo.Checkout));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(repo.Checkout));
        Assert.Contains("First wins.", TempGitRepository.ReadFile(repo.Checkout, "doc.md"), StringComparison.Ordinal);

        using var git2 = new Repository(repo.Checkout);
        Assert.NotNull(git2.Branches["agent/second"]);
    }

    [Fact]
    public async Task Resubmitting_an_identical_edit_is_a_no_change()
    {
        using var repo = new TempGitRepository(("doc.md", _base));
        using (var git = new Repository(repo.Checkout))
        {
            git.CreateBranch("agent/noop");
        }

        var writer = Writer();
        var edit = Edit(repo.Checkout, "agent/noop", "doc.md", _base);

        var result = await writer.SubmitAsync(edit, Ct);

        Assert.Equal(VaultEditOutcome.NoChange, result.Outcome);
        Assert.Equal(TempGitRepository.HeadOf(repo.Checkout), result.CommitSha);
    }

    [Fact]
    public async Task Decision_numbers_are_sequential_and_unique_under_concurrent_submits()
    {
        using var repo = new TempGitRepository(("index.md", "# Index\n"));
        var writer = Writer();
        var baseCommit = TempGitRepository.HeadOf(repo.Checkout);
        var requests = Enumerable.Range(1, 5).Select(i => new VaultEditRequest(
            repo.Checkout,
            $"agent/decision-{i}",
            null,
            $"choice-{i}",
            $"# Decision {i}\n",
            "Council",
            "council@example.invalid",
            baseCommit,
            null,
            null)).ToList();

        var results = await Task.WhenAll(requests.Select(request => writer.SubmitAsync(request, Ct)));

        Assert.All(results, result => Assert.Equal(VaultEditOutcome.Merged, result.Outcome));
        var numbers = results.Select(result => result.DecisionNumber!.Value).OrderBy(n => n).ToArray();
        Assert.Equal([1, 2, 3, 4, 5], numbers);
        foreach (var result in results)
        {
            var slug = $"choice-{result.DecisionNumber}";
            Assert.Equal($"decisions/{result.DecisionNumber:D4}-{slug}.md", result.Path);
            Assert.Equal($"# Decision {result.DecisionNumber}\n", TempGitRepository.ReadFile(repo.Checkout, result.Path!));
        }
    }

    [Fact]
    public async Task Paths_outside_the_vault_are_refused()
    {
        using var repo = new TempGitRepository(("index.md", "# Index\n"));
        var writer = Writer();

        await Assert.ThrowsAsync<ArgumentException>(() => writer.SubmitAsync(Edit(repo.Checkout, "agent/x", "../escape.md", "x"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.SubmitAsync(Edit(repo.Checkout, "agent/x", "notes/plain.txt", "x"), Ct));
    }
}
