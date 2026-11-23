using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Tests.Memory.Notes;
using Bazaar.Server.Tests.Memory.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>The rebuild from the Vault: idempotent, scoped to one agent, and failures never stop the rest.</summary>
public sealed class MemoryGraphRebuilderTests
{
    private const string _pathA = "agents/alpha/memory/lessons/retry-backoff.md";
    private const string _pathB = "agents/alpha/memory/lessons/second.md";
    private const string _otherAgentPath = "agents/beta/memory/lessons/other.md";
    private const string _secondId = "01J9ZABCDEFGHJKMNPQRSTVWXZ";
    private const string _thirdId = "01J9ZABCDEFGHJKMNPQRSTVWXA";

    [Fact]
    public async Task Rebuild_stores_a_revision_for_each_note_in_the_agents_area_only()
    {
        var vault = new InMemoryMemoryVault();
        vault.Seed(_pathA, NoteFixtures.Text());
        vault.Seed(_pathB, NoteFixtures.Text(Note(_secondId, "Second")));
        vault.Seed(_otherAgentPath, NoteFixtures.Text(Note(_thirdId, "Other")));
        var store = new FakeStore();

        var result = await Rebuilder(vault, store).RebuildAgentAsync("alpha", CancellationToken.None);

        Assert.Equal(new MemoryGraphRebuildResult(2, 2, 0, 0), result);
        Assert.Equal([NoteFixtures.NoteId, _secondId], store.Active.Keys.Order(StringComparer.Ordinal));
        Assert.All(store.Active.Values, r => Assert.Equal("alpha", r.ScopeAgentId));
    }

    [Fact]
    public async Task Rebuild_twice_is_idempotent()
    {
        var vault = new InMemoryMemoryVault();
        vault.Seed(_pathA, NoteFixtures.Text());
        var store = new FakeStore();
        var rebuilder = Rebuilder(vault, store);

        await rebuilder.RebuildAgentAsync("alpha", CancellationToken.None);
        var second = await rebuilder.RebuildAgentAsync("alpha", CancellationToken.None);

        Assert.Equal(new MemoryGraphRebuildResult(1, 0, 1, 0), second);
        Assert.Equal(1, store.StoreCalls);
    }

    [Fact]
    public async Task A_newer_vault_version_is_stored_over_the_older_active_revision()
    {
        var vault = new InMemoryMemoryVault();
        vault.Seed(_pathA, NoteFixtures.Text());
        var store = new FakeStore();
        var rebuilder = Rebuilder(vault, store);
        await rebuilder.RebuildAgentAsync("alpha", CancellationToken.None);

        vault.Seed(_pathA, NoteFixtures.Text(NoteFixtures.Note() with { Version = 2 }));
        var result = await rebuilder.RebuildAgentAsync("alpha", CancellationToken.None);

        Assert.Equal(1, result.Stored);
        Assert.Equal(2, store.Active[NoteFixtures.NoteId].Version);
    }

    [Fact]
    public async Task Unparsable_files_and_refused_stores_are_counted_and_the_rest_still_store()
    {
        var vault = new InMemoryMemoryVault();
        vault.Seed(_pathA, "no front matter here");
        vault.Seed(_pathB, NoteFixtures.Text(Note(_secondId, "Second")));
        vault.Seed("agents/alpha/memory/lessons/third.md", NoteFixtures.Text(Note(_thirdId, "Third")));
        var store = new FakeStore { Refuse = _secondId };

        var result = await Rebuilder(vault, store).RebuildAgentAsync("alpha", CancellationToken.None);

        Assert.Equal(new MemoryGraphRebuildResult(3, 1, 0, 2), result);
        Assert.Contains(_thirdId, store.Active.Keys);
    }

    [Fact]
    public async Task Unique_titles_resolve_wiki_links_and_ambiguous_titles_make_no_edge()
    {
        var vault = new InMemoryMemoryVault();
        vault.Seed(_pathA, NoteFixtures.Text(body: "See [[Second]] and [[Twin]]."));
        vault.Seed(_pathB, NoteFixtures.Text(Note(_secondId, "Second")));
        vault.Seed("agents/alpha/memory/lessons/t1.md", NoteFixtures.Text(Note(_thirdId, "Twin")));
        vault.Seed("agents/alpha/memory/lessons/t2.md", NoteFixtures.Text(Note("01J9ZABCDEFGHJKMNPQRSTVWXB", "Twin")));
        var store = new FakeStore();

        await Rebuilder(vault, store).RebuildAgentAsync("alpha", CancellationToken.None);

        var links = store.Active[NoteFixtures.NoteId].Edges.Where(e => e.Kind == MemoryEdgeKind.Link).ToList();
        Assert.Equal([_secondId], links.Select(e => e.TargetId));
    }

    [Fact]
    public async Task An_invalid_agent_id_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Rebuilder(new InMemoryMemoryVault(), new FakeStore()).RebuildAgentAsync("../beta", CancellationToken.None));
    }

    private static MemoryGraphRebuilder Rebuilder(InMemoryMemoryVault vault, FakeStore store) =>
        new(vault, store, NullLogger<MemoryGraphRebuilder>.Instance);

    private static MemoryNote Note(string id, string title) => NoteFixtures.Note() with { Id = id, Title = title };

    private sealed class FakeStore : IMemoryGraphStore
    {
        public Dictionary<string, MemoryGraphRevision> Active { get; } = new(StringComparer.Ordinal);

        public int StoreCalls { get; private set; }

        public string? Refuse { get; init; }

        public Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
        {
            if (revision.NoteId == Refuse)
            {
                throw new InvalidOperationException("refused");
            }

            StoreCalls++;
            Active[revision.NoteId] = revision;
            return Task.CompletedTask;
        }

        public Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>(noteIds.Where(Active.ContainsKey).Select(id => Active[id]).ToList());

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);
    }
}
