namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// The kind of an edge in the memory graph (docs/design/constellation-memory-graph.md, "Records").
/// Only <see cref="Link"/>, <see cref="DerivedFrom"/> and <see cref="Supersedes"/> join two notes, so only they expand a
/// retrieval. The provenance kinds point at non-note nodes and only label a hit.
/// </summary>
public enum MemoryEdgeKind
{
    /// <summary>A <c>[[wiki-link]]</c> in the note body that resolves to another note.</summary>
    Link,

    /// <summary>A note the note was derived from (<c>MemoryNote.DerivedFrom</c>).</summary>
    DerivedFrom,

    /// <summary>A note this note replaces (<c>MemoryNote.Supersedes</c>).</summary>
    Supersedes,

    /// <summary>The agent that wrote the note. The target is <c>agent:&lt;id&gt;</c>.</summary>
    AuthoredBy,

    /// <summary>The GC the note came from. The target is <c>gc:&lt;id&gt;</c>.</summary>
    FromGc,

    /// <summary>A GC message the note came from. The target is <c>message:&lt;id&gt;</c>.</summary>
    FromMessage,
}

/// <summary>One outgoing edge of a memory revision.</summary>
/// <param name="TargetId">A note id, or a prefixed id (<c>agent:</c>, <c>gc:</c>, <c>message:</c>) for provenance.</param>
/// <param name="Kind">The edge kind.</param>
/// <param name="Weight">Expansion weight in [0, 1]. Provenance edges use 0.</param>
public sealed record MemoryGraphEdge(string TargetId, MemoryEdgeKind Kind, double Weight);

/// <summary>
/// One stored revision of a memory note as the graph sees it. Revisions are append-only; exactly one revision per note
/// is active at a time.
/// </summary>
/// <param name="NoteId">The note id (<c>MemoryNote.Id</c>).</param>
/// <param name="Version">The note version (<c>MemoryNote.Version</c>).</param>
/// <param name="ScopeAgentId">The agent whose private area holds the note. Access is decided through the Vault access layer.</param>
/// <param name="VaultPath">The normalized vault path (<c>VaultPath.Value</c>).</param>
/// <param name="Title">The note title.</param>
/// <param name="Kind">The <c>MemoryKind</c> name.</param>
/// <param name="Status">The <c>MemoryNoteStatus</c> name.</param>
/// <param name="Trust">The <c>MemoryTier</c> name.</param>
/// <param name="Edges">The outgoing edges of this revision.</param>
public sealed record MemoryGraphRevision(
    string NoteId,
    long Version,
    string ScopeAgentId,
    string VaultPath,
    string Title,
    string Kind,
    string Status,
    string Trust,
    IReadOnlyList<MemoryGraphEdge> Edges);

/// <summary>Knobs for one retrieval. Defaults are the shipped values.</summary>
/// <param name="MaxHops">How far to expand from a seed. Capped at 2 by the retriever.</param>
/// <param name="TopK">The number of fused seeds kept before expansion.</param>
/// <param name="RrfK">The RRF constant (see <c>RetrievalOptions</c>).</param>
public sealed record MemoryGraphOptions(int MaxHops = 1, int TopK = 20, int RrfK = 60);

/// <summary>One note returned by a retrieval, with the score it was packed under.</summary>
/// <param name="NoteId">The note id.</param>
/// <param name="VaultPath">The vault path the caller may read.</param>
/// <param name="Title">The note title.</param>
/// <param name="ScopeAgentId">The agent scope of the note.</param>
/// <param name="Kind">The <c>MemoryKind</c> name.</param>
/// <param name="Trust">The <c>MemoryTier</c> name.</param>
/// <param name="Score">The score after expansion weighting.</param>
/// <param name="HopDistance">0 for a seed, 1 or 2 for an expansion hit.</param>
public sealed record MemoryGraphHit(
    string NoteId,
    string VaultPath,
    string Title,
    string ScopeAgentId,
    string Kind,
    string Trust,
    double Score,
    int HopDistance);

/// <summary>A retrieval result. <paramref name="LexicalOnly"/> is true when no vector seeds were supplied.</summary>
/// <param name="Hits">The packed hits, best first.</param>
/// <param name="LexicalOnly">True when the seeds came from lexical search only.</param>
public sealed record MemoryGraphRetrieval(IReadOnlyList<MemoryGraphHit> Hits, bool LexicalOnly);
