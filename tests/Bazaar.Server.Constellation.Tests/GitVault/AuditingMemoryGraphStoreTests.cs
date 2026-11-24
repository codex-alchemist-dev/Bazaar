using System.Text.Json.Nodes;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Memory.GitVault;

/// <summary>Each memory graph change is audited with ids and provenance only, and audit failures are not hidden.</summary>
public sealed class AuditingMemoryGraphStoreTests
{
    private readonly FakeInner _inner = new();
    private readonly CapturingLog _log = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_stored_revision_is_audited_with_provenance_and_no_title_or_path()
    {
        var store = new AuditingMemoryGraphStore(_inner, _log, _time);

        await store.StoreRevisionAsync(Revision(2), CancellationToken.None);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(AuditingMemoryGraphStore.StoredKind, entry.Kind);
        Assert.Equal("agent:alpha", entry.Actor);
        Assert.Equal(_time.GetUtcNow(), entry.Ts);
        var payload = JsonNode.Parse(entry.Payload)!.AsObject();
        Assert.Equal("note-1", (string?)payload["note_id"]);
        Assert.Equal(2, (long?)payload["version"]);
        Assert.Equal("agent:alpha", (string?)payload["written_by"]);
        Assert.Equal("gc:gc-1", (string?)payload["from_gc"]);
        Assert.Equal(["message:m1", "message:m2"], payload["from_messages"]!.AsArray().Select(n => (string?)n));
        Assert.DoesNotContain("Secret title", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("agents/alpha", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rollback_is_audited_as_the_system_actor_with_the_activated_revisions_provenance()
    {
        _inner.Active["note-1"] = Revision(1);
        var store = new AuditingMemoryGraphStore(_inner, _log, _time);

        var activated = await store.ActivateRevisionAsync("note-1", 1, CancellationToken.None);

        Assert.True(activated);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(AuditingMemoryGraphStore.ActivatedKind, entry.Kind);
        Assert.Equal(AuditingMemoryGraphStore.SystemActor, entry.Actor);
        Assert.Equal(1, (long?)JsonNode.Parse(entry.Payload)!["version"]);
    }

    [Fact]
    public async Task A_missing_version_is_not_audited()
    {
        var store = new AuditingMemoryGraphStore(_inner, _log, _time);

        Assert.False(await store.ActivateRevisionAsync("note-1", 9, CancellationToken.None));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task A_failed_store_is_not_audited()
    {
        _inner.FailStore = true;
        var store = new AuditingMemoryGraphStore(_inner, _log, _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreRevisionAsync(Revision(1), CancellationToken.None));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task An_audit_failure_is_reported_after_the_revision_is_stored()
    {
        _log.Fail = true;
        var store = new AuditingMemoryGraphStore(_inner, _log, _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreRevisionAsync(Revision(1), CancellationToken.None));
        Assert.Single(_inner.Stored);
    }

    [Fact]
    public async Task Reads_pass_through()
    {
        _inner.Active["note-1"] = Revision(1);
        var store = new AuditingMemoryGraphStore(_inner, _log, _time);

        Assert.Single(await store.GetActiveAsync(["note-1"], CancellationToken.None));
        Assert.Empty(_log.Entries);
    }

    private static MemoryGraphRevision Revision(long version) => new(
        "note-1",
        version,
        "alpha",
        "agents/alpha/memory/x.md",
        "Secret title",
        "Procedural",
        "Active",
        "AgentVerified",
        [
            new MemoryGraphEdge("agent:alpha", MemoryEdgeKind.AuthoredBy, 0),
            new MemoryGraphEdge("gc:gc-1", MemoryEdgeKind.FromGc, 0),
            new MemoryGraphEdge("message:m1", MemoryEdgeKind.FromMessage, 0),
            new MemoryGraphEdge("message:m2", MemoryEdgeKind.FromMessage, 0),
        ]);

    private sealed class CapturingLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool Fail { get; set; }

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("audit unavailable");
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInner : IMemoryGraphStore
    {
        public Dictionary<string, MemoryGraphRevision> Active { get; } = new(StringComparer.Ordinal);

        public List<MemoryGraphRevision> Stored { get; } = [];

        public bool FailStore { get; set; }

        public Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
        {
            if (FailStore)
            {
                throw new InvalidOperationException("store failed");
            }

            Stored.Add(revision);
            Active[revision.NoteId] = revision;
            return Task.CompletedTask;
        }

        public Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct) =>
            Task.FromResult(Active.TryGetValue(noteId, out var r) && r.Version == version);

        public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>(noteIds.Where(Active.ContainsKey).Select(id => Active[id]).ToList());

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsByDocAsync(Guid docId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryGraphRevision>>([]);
    }
}
