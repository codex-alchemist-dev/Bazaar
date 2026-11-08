using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>Decision immutability (doc 18, work package 9): frozen records refuse edits, deletes and renames.</summary>
public sealed class DecisionImmutabilityTests : IDisposable
{
    private const string _acceptedPath = "decisions/0001-use-postgres.md";
    private static readonly Guid _docId = Guid.Parse("0196a7c4-0000-7000-8000-000000000009");

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;

    public DecisionImmutabilityTests()
    {
        _repo = new TempGitRepository(("doc.md", "line 1\n"));
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task Editing_a_proposed_decision_is_allowed()
    {
        await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "use-postgres", "Use PostgreSQL", "We keep Postgres.", "agent-ana"),
            CancellationToken.None);
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewBranchAsync("draft-edit");
        var worktree = _repo.WorktreePath("draft-edit");
        TempGitRepository.WriteFile(worktree, _acceptedPath, Decision("proposed", "We keep Postgres, with pgvector."));
        TempGitRepository.CommitAll(worktree, "refine draft");

        var result = await Submit(baseCommit, "draft-edit");

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
    }

    [Fact]
    public async Task Editing_the_body_of_an_accepted_decision_is_refused_and_main_is_untouched()
    {
        await AcceptedDecisionOnMainAsync();
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        var mainHead = TempGitRepository.HeadOf(_repo.Checkout);
        await NewBranchAsync("rewrite");
        var worktree = _repo.WorktreePath("rewrite");
        TempGitRepository.WriteFile(worktree, _acceptedPath, Decision("accepted", "We switched to something else."));
        TempGitRepository.CommitAll(worktree, "rewrite decision");

        var result = await Submit(baseCommit, "rewrite");

        Assert.Equal(VaultEditOutcome.Refused, result.Outcome);
        Assert.Equal([_acceptedPath], result.ConflictPaths);
        Assert.Equal(mainHead, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task Superseding_an_accepted_decision_by_status_alone_is_allowed()
    {
        await AcceptedDecisionOnMainAsync();
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewBranchAsync("supersede");
        var worktree = _repo.WorktreePath("supersede");
        TempGitRepository.WriteFile(worktree, _acceptedPath, Decision("superseded", "We keep Postgres."));
        TempGitRepository.CommitAll(worktree, "supersede decision");

        var result = await Submit(baseCommit, "supersede");

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
    }

    [Fact]
    public async Task Deleting_an_accepted_decision_is_refused()
    {
        await AcceptedDecisionOnMainAsync();
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewBranchAsync("delete");
        var worktree = _repo.WorktreePath("delete");
        File.Delete(Path.Combine(worktree, _acceptedPath));
        TempGitRepository.CommitAll(worktree, "delete decision");

        var result = await Submit(baseCommit, "delete");

        Assert.Equal(VaultEditOutcome.Refused, result.Outcome);
        Assert.Equal([_acceptedPath], result.ConflictPaths);
    }

    [Fact]
    public async Task Adding_a_new_decision_is_allowed_beside_frozen_ones()
    {
        await AcceptedDecisionOnMainAsync();
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await NewBranchAsync("another");
        var worktree = _repo.WorktreePath("another");
        TempGitRepository.WriteFile(worktree, "decisions/0002-vault-in-git.md", Decision("proposed", "Markdown in git."));
        TempGitRepository.CommitAll(worktree, "add decision");

        var result = await Submit(baseCommit, "another");

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
    }

    /// <summary>Puts an accepted decision on main, the state a frozen record is in.</summary>
    private async Task AcceptedDecisionOnMainAsync()
    {
        await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "use-postgres", "Use PostgreSQL", "We keep Postgres.", "agent-ana"),
            CancellationToken.None);
        TempGitRepository.WriteFile(_repo.Checkout, _acceptedPath, Decision("accepted", "We keep Postgres."));
        TempGitRepository.CommitAll(_repo.Checkout, "accept decision");
    }

    private static string Decision(string status, string body) =>
        "---\nid: 0196a7c4-0000-7000-8000-000000000042\ntype: decision\ntitle: Use PostgreSQL\n"
        + $"status: {status}\n---\n\n{body}\n";

    private async Task NewBranchAsync(string task) =>
        await _git.AddWorktreeAsync(_repo.Checkout, "agent/" + task, _repo.WorktreePath(task), CancellationToken.None);

    private Task<VaultEditResult> Submit(string baseCommit, string task) =>
        _writer.SubmitAsync(
            new VaultEditRequest(_docId, baseCommit, "agent/" + task, "agent-ana", null, null),
            CancellationToken.None);
}
