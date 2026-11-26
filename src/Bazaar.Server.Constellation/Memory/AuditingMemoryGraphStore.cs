using System.Text.Json.Nodes;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.PrivacyAudit;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>
/// Writes each memory graph change to the audit log (docs/design/constellation-memory-graph.md, "Audit"; doc 19, "Agent memory
/// governance"). The payload carries ids and provenance only, never titles, bodies or vault paths.
/// </summary>
public sealed class AuditingMemoryGraphStore(IMemoryGraphStore inner, IAuditLog log, TimeProvider time) : IMemoryGraphStore
{
    public const string StoredKind = "memory.revision.stored";
    public const string ActivatedKind = "memory.revision.activated";

    /// <summary>The actor of a rollback: the store interface carries no acting principal yet.</summary>
    public const string SystemActor = "system:memory";

    private readonly IMemoryGraphStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IAuditLog _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <inheritdoc />
    public async Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
    {
        await _inner.StoreRevisionAsync(revision, ct).ConfigureAwait(false);
        await AppendAsync(StoredKind, $"agent:{revision.ScopeAgentId}", revision, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct)
    {
        var activated = await _inner.ActivateRevisionAsync(noteId, version, ct).ConfigureAwait(false);
        if (!activated)
        {
            return false;
        }

        var revisions = await _inner.GetActiveAsync([noteId], ct).ConfigureAwait(false);
        var revision = revisions.FirstOrDefault(r => r.Version == version)
            ?? throw new InvalidOperationException("The activated revision could not be read back for the audit entry.");
        await AppendAsync(ActivatedKind, SystemActor, revision, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct) =>
        _inner.GetActiveAsync(noteIds, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
        _inner.ListRevisionsAsync(noteId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsByDocAsync(Guid docId, CancellationToken ct) =>
        _inner.ListRevisionsByDocAsync(docId, ct);

    private Task AppendAsync(string kind, string actor, MemoryGraphRevision revision, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["note_id"] = revision.NoteId,
            ["version"] = revision.Version,
            ["scope_agent_id"] = revision.ScopeAgentId,
            ["memory_kind"] = revision.Kind,
            ["trust"] = revision.Trust,
            ["written_by"] = Single(revision, MemoryEdgeKind.AuthoredBy),
            ["from_gc"] = Single(revision, MemoryEdgeKind.FromGc),
            ["from_messages"] = new JsonArray(revision.Edges
                .Where(e => e.Kind == MemoryEdgeKind.FromMessage)
                .Select(e => (JsonNode?)JsonValue.Create(e.TargetId))
                .ToArray()),
        };
        AuditPayloadGuard.Ensure(payload);
        return _log.AppendAsync(new AuditEntry(_time.GetUtcNow(), actor, kind, payload.ToJsonString()), ct);
    }

    private static string? Single(MemoryGraphRevision revision, MemoryEdgeKind kind) =>
        revision.Edges.FirstOrDefault(e => e.Kind == kind)?.TargetId;
}
