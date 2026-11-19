using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Tests.Memory.Notes;
using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Tests.Memory.GitVault;

/// <summary>The Vault-backed memory vault, over a real git repository, the real git provider and the real writer.</summary>
public sealed class GitMemoryVaultTests : IDisposable
{
    private const string _notePath = "agents/alpha/memory/lessons/retry-backoff.md";
    private const string _secondId = "01J9ZABCDEFGHJKMNPQRSTVWXZ";

    private readonly TempGitRepository _repo = new(("Index.md", "# Vault\n"));
    private readonly string _worktrees = Path.Combine(Path.GetTempPath(), "bazaar-memvault-" + Guid.NewGuid().ToString("N"));
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;
    private readonly FakeLexicalIndex _lexical = new();

    public GitMemoryVaultTests() => _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer);

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
        if (Directory.Exists(_worktrees))
        {
            Directory.Delete(_worktrees, recursive: true);
        }
    }

    [Fact]
    public async Task A_committed_batch_lands_on_main_with_trailers_and_reads_back()
    {
        using var vault = Vault(_writer);
        var before = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await vault.CommitAsync(Batch(Item(_notePath, NoteFixtures.Text())), CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEqual(before, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(result.CommitSha, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(NoteFixtures.Text(), (await vault.ReadNoteAsync(VaultPath.Parse(_notePath), CancellationToken.None))?.Replace("\r\n", "\n"));
        Assert.Contains("Bazaar-Agent: alpha", await HeadMessageAsync());
    }

    [Fact]
    public async Task A_missing_note_reads_as_null()
    {
        using var vault = Vault(_writer);

        Assert.Null(await vault.ReadNoteAsync(VaultPath.Parse(_notePath), CancellationToken.None));
    }

    [Fact]
    public async Task A_write_outside_the_agents_own_memory_area_fails_the_batch_and_writes_nothing()
    {
        using var vault = Vault(_writer);
        var before = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await vault.CommitAsync(
            Batch(Item(_notePath, NoteFixtures.Text()), Item("agents/beta/memory/x.md", NoteFixtures.Text())),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(before, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Null(await vault.ReadNoteAsync(VaultPath.Parse(_notePath), CancellationToken.None));
    }

    [Fact]
    public async Task A_conflict_is_a_failure_that_keeps_the_branch_and_removes_the_worktree()
    {
        using var vault = Vault(new FixedWriter(VaultEditOutcome.Conflicted, ["agents/alpha/memory/lessons/retry-backoff.md"]));

        var result = await vault.CommitAsync(Batch(Item(_notePath, NoteFixtures.Text())), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Conflict", result.FailureReason);
        Assert.Empty(Directory.GetDirectories(_worktrees));
    }

    [Fact]
    public async Task A_refused_batch_is_a_failure()
    {
        using var vault = Vault(new FixedWriter(VaultEditOutcome.Refused, ["decisions/0001-x.md"]));

        var result = await vault.CommitAsync(Batch(Item(_notePath, NoteFixtures.Text())), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task List_returns_paths_under_the_prefix_in_order()
    {
        using var vault = Vault(_writer);
        await vault.CommitAsync(
            Batch(
                Item("agents/alpha/memory/lessons/b.md", NoteFixtures.Text()),
                Item("agents/alpha/memory/lessons/a.md", NoteFixtures.Text(Note(_secondId)))),
            CancellationToken.None);

        var paths = await vault.ListAsync("agents/alpha/memory/", CancellationToken.None);

        Assert.Equal(["agents/alpha/memory/lessons/a.md", "agents/alpha/memory/lessons/b.md"], paths.Select(p => p.Value));
    }

    [Fact]
    public async Task Find_by_id_follows_the_head_commit()
    {
        using var vault = Vault(_writer);
        Assert.Null(await vault.FindNoteByIdAsync(NoteFixtures.NoteId, CancellationToken.None));

        await vault.CommitAsync(Batch(Item(_notePath, NoteFixtures.Text())), CancellationToken.None);

        var found = await vault.FindNoteByIdAsync(NoteFixtures.NoteId, CancellationToken.None);
        Assert.Equal(_notePath, found?.Value);
        Assert.Null(await vault.FindNoteByIdAsync(_secondId, CancellationToken.None));
    }

    [Fact]
    public async Task Search_goes_to_the_lexical_index()
    {
        using var vault = Vault(_writer);
        _lexical.Hits = [new RankedHit(_notePath, 2.5)];

        var hits = await vault.SearchLexicalAsync("backoff", "agents/alpha/memory/", 5, CancellationToken.None);

        Assert.Equal(_notePath, Assert.Single(hits).Id);
        Assert.Equal(("backoff", "agents/alpha/memory/", 5), _lexical.LastCall);
    }

    [Fact]
    public async Task An_empty_batch_and_an_invalid_agent_id_fail()
    {
        using var vault = Vault(_writer);

        Assert.False((await vault.CommitAsync(MemoryBatch.Create("alpha", []), CancellationToken.None)).Succeeded);
        Assert.False((await vault.CommitAsync(
            MemoryBatch.Create("../beta", [Item("agents/../beta/memory/x.md", NoteFixtures.Text())]), CancellationToken.None)).Succeeded);
    }

    private GitMemoryVault Vault(IVaultWriter writer) =>
        new(_git, writer, _lexical, new GitMemoryVaultOptions(_repo.Checkout, _worktrees, agent => new GitIdentity(agent, agent + "@example.invalid")));

    private async Task<string> HeadMessageAsync()
    {
        var commits = await _git.ListCommitsAsync(_repo.Checkout, null, TempGitRepository.HeadOf(_repo.Checkout), CancellationToken.None);
        return string.Join('\n', commits.Select(c => c.Message));
    }

    private static MemoryBatch Batch(params MemoryWriteItem[] items) => MemoryBatch.Create("alpha", items);

    private static MemoryWriteItem Item(string path, string content) => new(Guid.NewGuid(), path, content, null, null, "ok");

    private static MemoryNote Note(string id) => NoteFixtures.Note() with { Id = id };

    private sealed class FakeLexicalIndex : IMemoryLexicalIndex
    {
        public IReadOnlyList<RankedHit> Hits { get; set; } = [];

        public (string Query, string Area, int Max)? LastCall { get; private set; }

        public Task<IReadOnlyList<RankedHit>> SearchAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct)
        {
            LastCall = (queryText, areaPrefix, maxCandidates);
            return Task.FromResult(Hits);
        }
    }

    private sealed class FixedWriter(VaultEditOutcome outcome, IReadOnlyList<string> paths) : IVaultWriter
    {
        public Task<VaultEditResult> SubmitAsync(VaultEditRequest request, CancellationToken ct) =>
            Task.FromResult(new VaultEditResult(outcome, "old", "old", paths, null));
    }
}
