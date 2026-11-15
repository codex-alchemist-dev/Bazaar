using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>The indexer hook: every landed commit is reported with its head before and after (doc 18, "Indexing").</summary>
public sealed class VaultWriterObserverTests : IDisposable
{
    private static readonly Guid _docId = Guid.Parse("0196a7c4-0000-7000-8000-00000000000c");

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly RecordingObserver _observer = new();
    private readonly VaultWriter _writer;

    public VaultWriterObserverTests()
    {
        _repo = new TempGitRepository(("doc.md", "line 1\nline 2\n"));
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer, _observer);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task A_landed_decision_is_reported_with_the_head_before_and_after()
    {
        var before = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "observed", "Observed", "Body.", "agent-ana"),
            CancellationToken.None);

        var seen = Assert.Single(_observer.Commits);
        Assert.Equal(before, seen.Old);
        Assert.Equal(result.NewCommit, seen.New);
        Assert.Equal(TempGitRepository.HeadOf(_repo.Checkout), seen.New);
    }

    [Fact]
    public async Task A_merged_branch_is_reported()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await _git.AddWorktreeAsync(_repo.Checkout, "agent/obs", _repo.WorktreePath("obs"), CancellationToken.None);
        TempGitRepository.WriteFile(_repo.WorktreePath("obs"), "doc.md", "line 1\nagent 2\n");
        TempGitRepository.CommitAll(_repo.WorktreePath("obs"), "edit");

        var result = await _writer.SubmitAsync(
            new VaultEditRequest(_docId, baseCommit, "agent/obs", "agent-ana", null, null),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
        var seen = Assert.Single(_observer.Commits);
        Assert.Equal(baseCommit, seen.Old);
        Assert.Equal(result.NewCommit, seen.New);
    }

    [Fact]
    public async Task A_refused_or_up_to_date_submission_is_not_reported()
    {
        var head = TempGitRepository.HeadOf(_repo.Checkout);
        await _git.AddWorktreeAsync(_repo.Checkout, "agent/same", _repo.WorktreePath("same"), CancellationToken.None);

        var result = await _writer.SubmitAsync(
            new VaultEditRequest(_docId, head, "agent/same", "agent-ana", null, null),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.UpToDate, result.Outcome);
        Assert.Empty(_observer.Commits);
    }

    private sealed class RecordingObserver : IVaultCommitObserver
    {
        public List<(string Old, string New)> Commits { get; } = [];

        public Task OnCommitAsync(string oldCommit, string newCommit, CancellationToken ct)
        {
            Commits.Add((oldCommit, newCommit));
            return Task.CompletedTask;
        }
    }
}
