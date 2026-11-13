using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>The conflict card (doc 18, Write path step 5): both sides of each conflicting document and the actions.</summary>
public sealed class VaultConflictCardTests : IDisposable
{
    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();

    public VaultConflictCardTests()
    {
        _repo = new TempGitRepository(("doc.md", "line 1\nline 2\n"));
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task The_card_carries_base_main_and_branch_text_for_each_conflicting_path()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        TempGitRepository.WriteFile(_repo.Checkout, "doc.md", "line 1\nmain edit\n");
        TempGitRepository.CommitAll(_repo.Checkout, "main edit");
        var mainCommit = TempGitRepository.HeadOf(_repo.Checkout);

        await _git.AddWorktreeAsync(_repo.Checkout, "agent/card", _repo.WorktreePath("card"), CancellationToken.None);
        var worktree = _repo.WorktreePath("card");
        TempGitRepository.WriteFile(worktree, "doc.md", "line 1\nbranch edit\n");
        TempGitRepository.CommitAll(worktree, "branch edit");
        var branchCommit = TempGitRepository.HeadOf(worktree);

        var card = await VaultConflictCards.BuildAsync(
            _git, _repo.Checkout, baseCommit, mainCommit, branchCommit, ["doc.md"], null, CancellationToken.None);

        var file = Assert.Single(card.Files);
        Assert.Equal("doc.md", file.Path);
        Assert.Equal("line 1\nline 2\n", file.Base);
        Assert.Equal("line 1\nmain edit\n", file.Main);
        Assert.Equal("line 1\nbranch edit\n", file.Branch);
        Assert.Equal(VaultConflictCards.DefaultActions, card.Actions);
    }

    [Fact]
    public async Task A_path_missing_on_one_side_reads_as_null_there()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        TempGitRepository.WriteFile(_repo.Checkout, "new.md", "added on main\n");
        TempGitRepository.CommitAll(_repo.Checkout, "add on main");
        var mainCommit = TempGitRepository.HeadOf(_repo.Checkout);

        var card = await VaultConflictCards.BuildAsync(
            _git, _repo.Checkout, baseCommit, mainCommit, baseCommit, ["new.md"], ["keep mine"], CancellationToken.None);

        var file = Assert.Single(card.Files);
        Assert.Null(file.Base);
        Assert.Equal("added on main\n", file.Main);
        Assert.Null(file.Branch);
        Assert.Equal(["keep mine"], card.Actions);
    }
}
