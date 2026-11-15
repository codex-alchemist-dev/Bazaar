using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>The conflict card is posted to a GC only for a conflicted merge (doc 18, Write path step 5).</summary>
public sealed class VaultConflictCardPublisherTests : IDisposable
{
    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly RecordingPoster _poster = new();

    public VaultConflictCardPublisherTests()
    {
        _repo = new TempGitRepository(("doc.md", "line 1\nline 2\n"));
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task A_conflicted_result_posts_one_card_with_both_sides()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        TempGitRepository.WriteFile(_repo.Checkout, "doc.md", "line 1\nmain edit\n");
        TempGitRepository.CommitAll(_repo.Checkout, "main edit");
        var mainCommit = TempGitRepository.HeadOf(_repo.Checkout);

        await _git.AddWorktreeAsync(_repo.Checkout, "agent/pub", _repo.WorktreePath("pub"), CancellationToken.None);
        var worktree = _repo.WorktreePath("pub");
        TempGitRepository.WriteFile(worktree, "doc.md", "line 1\nbranch edit\n");
        TempGitRepository.CommitAll(worktree, "branch edit");
        var branchCommit = TempGitRepository.HeadOf(worktree);

        var result = new VaultEditResult(VaultEditOutcome.Conflicted, mainCommit, mainCommit, ["doc.md"], null);
        var publisher = new VaultConflictCardPublisher(_git, _poster);

        var posted = await publisher.PublishAsync(
            result, _repo.Checkout, baseCommit, branchCommit, "gc-1", "task-9", null, CancellationToken.None);

        Assert.True(posted);
        var call = Assert.Single(_poster.Calls);
        Assert.Equal("gc-1", call.GcId);
        Assert.Equal("task-9", call.TaskId);
        var file = Assert.Single(call.Card.Files);
        Assert.Equal("line 1\nline 2\n", file.Base);
        Assert.Equal("line 1\nmain edit\n", file.Main);
        Assert.Equal("line 1\nbranch edit\n", file.Branch);
        Assert.Equal(VaultConflictCards.DefaultActions, call.Card.Actions);
    }

    [Fact]
    public async Task A_merged_result_posts_nothing()
    {
        var head = TempGitRepository.HeadOf(_repo.Checkout);
        var result = new VaultEditResult(VaultEditOutcome.Merged, head, head, [], null);
        var publisher = new VaultConflictCardPublisher(_git, _poster);

        var posted = await publisher.PublishAsync(
            result, _repo.Checkout, head, head, "gc-1", null, null, CancellationToken.None);

        Assert.False(posted);
        Assert.Empty(_poster.Calls);
    }

    private sealed class RecordingPoster : IGcCardPoster
    {
        public List<(string GcId, string? TaskId, VaultConflictCard Card)> Calls { get; } = [];

        public Task PostConflictCardAsync(string gcId, string? taskId, VaultConflictCard card, CancellationToken ct)
        {
            Calls.Add((gcId, taskId, card));
            return Task.CompletedTask;
        }
    }
}
