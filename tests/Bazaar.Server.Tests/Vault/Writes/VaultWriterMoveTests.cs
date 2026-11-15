using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Search;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>Move and rename with link rewrite (doc 18, work package 8).</summary>
public sealed class VaultWriterMoveTests : IDisposable
{
    private static readonly Guid _docId = Guid.Parse("0196a7c4-0000-7000-8000-00000000000b");
    private const string _otherId = "01J5Q2R0000000000000000000";

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;

    public VaultWriterMoveTests()
    {
        _repo = new TempGitRepository(
            ("other.md", $"---\nid: {_otherId}\ntype: memory\ntitle: Other Doc\n---\n\nOther body.\n"),
            ("doc.md", "See [[other]] and [[other.md]] and [[Other Doc]].\n"),
            ("ref.md", "Links [[other#Section|alias]] and ![[other]].\n"));
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task Moving_a_document_rewrites_path_links_and_keeps_title_links()
    {
        var result = await _writer.MoveAsync(
            new VaultMoveRequest(_docId, "other.md", "archive/other.md", "agent-ana", null, null, VaultCaller.Owner),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_repo.Checkout, "other.md")));
        Assert.True(File.Exists(Path.Combine(_repo.Checkout, "archive", "other.md")));
        Assert.Equal("See [[archive/other]] and [[archive/other.md]] and [[Other Doc]].\n",
            TempGitRepository.ReadFile(_repo.Checkout, "doc.md"));
        Assert.Equal("Links [[archive/other#Section|alias]] and ![[archive/other]].\n",
            TempGitRepository.ReadFile(_repo.Checkout, "ref.md"));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.Equal(result.NewCommit, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task Moving_onto_an_existing_path_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.MoveAsync(
            new VaultMoveRequest(_docId, "other.md", "doc.md", "agent-ana", null, null, VaultCaller.Owner),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/abs.md")]
    [InlineData("notes/plain.txt")]
    [InlineData(".git/config.md")]
    public async Task Paths_must_stay_inside_the_vault(string toPath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _writer.MoveAsync(
            new VaultMoveRequest(_docId, "other.md", toPath, "agent-ana", null, null, VaultCaller.Owner),
            CancellationToken.None));
    }

    private const string _agentA = "agent:0196a7c4-0000-7000-8000-0000000000a1";
    private const string _agentB = "agent:0196a7c4-0000-7000-8000-0000000000b2";

    [Fact]
    public async Task An_agent_may_move_a_shared_document_when_no_private_document_links_to_it()
    {
        var result = await _writer.MoveAsync(
            new VaultMoveRequest(_docId, "other.md", "archive/other.md", "agent-ana", null, null, new VaultCaller(false, _agentA)),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
    }

    [Fact]
    public async Task An_agent_cannot_move_a_private_document_of_another_agent()
    {
        TempGitRepository.WriteFile(_repo.Checkout, "memory/b.md", PrivateNote(_agentB, "[[other]]"));
        TempGitRepository.CommitAll(_repo.Checkout, "agent b memory");
        var head = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.MoveAsync(
            new VaultMoveRequest(_docId, "memory/b.md", "memory/moved.md", "agent-ana", null, null, new VaultCaller(false, _agentA)),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Refused, result.Outcome);
        Assert.Equal(head, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task A_move_is_refused_whole_when_a_private_document_of_another_agent_links_to_it()
    {
        TempGitRepository.WriteFile(_repo.Checkout, "memory/b.md", PrivateNote(_agentB, "Secret link to [[other]]."));
        TempGitRepository.CommitAll(_repo.Checkout, "agent b memory");
        var head = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.MoveAsync(
            new VaultMoveRequest(_docId, "other.md", "archive/other.md", "agent-ana", null, null, new VaultCaller(false, _agentA)),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Refused, result.Outcome);
        Assert.Equal(["other.md"], result.ConflictPaths);
        Assert.Equal(head, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.True(File.Exists(Path.Combine(_repo.Checkout, "other.md")));
        Assert.Equal("Secret link to [[other]].\n", TempGitRepository.ReadFile(_repo.Checkout, "memory/b.md").Split("---\n\n").Last());
    }

    private static string PrivateNote(string owner, string body) =>
        "---\nid: 01J5Q2R0000000000000000001\ntype: memory\ntitle: Private note\nowner: " + owner + "\n---\n\n" + body + "\n";

    [Fact]
    public async Task Moving_a_frozen_decision_is_refused_and_leaves_the_file_in_place()
    {
        await _writer.CreateDecisionAsync(
            new DecisionDraft(Guid.NewGuid(), "use-postgres", "Use PostgreSQL", "We keep Postgres.", "agent-ana"),
            CancellationToken.None);
        TempGitRepository.WriteFile(
            _repo.Checkout,
            "decisions/0001-use-postgres.md",
            "---\nid: 0196a7c4-0000-7000-8000-000000000042\ntype: decision\ntitle: Use PostgreSQL\nstatus: accepted\n---\n\nWe keep Postgres.\n");
        TempGitRepository.CommitAll(_repo.Checkout, "accept decision");
        var head = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _writer.MoveAsync(
            new VaultMoveRequest(_docId, "decisions/0001-use-postgres.md", "decisions/moved.md", "agent-ana", null, null, VaultCaller.Owner),
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Refused, result.Outcome);
        Assert.Equal(head, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.True(File.Exists(Path.Combine(_repo.Checkout, "decisions", "0001-use-postgres.md")));
    }
}
