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
/// is active at a time. Since the convergence (docs/design/constellation-memory-graph.md, "Convergence design") a revision is
/// a set of attributes of its Vault doc node, <paramref name="DocId"/>, not a node of its own.
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
/// <param name="DocId">
/// The Vault index doc (<c>vault_docs.doc_id</c>) this note is. Null when the Vault index had not seen the file when the revision
/// was stored; the store resolves it on write and <see cref="MemoryGraphDocIdMigrator"/> backfills it.
/// </param>
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
    IReadOnlyList<MemoryGraphEdge> Edges,
    Guid? DocId = null);

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
/// <param name="IncludeHistory">
/// When true, notes superseded by a readable current head are returned as labelled history (<see cref="MemoryGraphHit.IsHistory"/>),
/// always ranked below their head. When false (the default), only the head of a supersedes chain is returned.
/// </param>
/// <param name="ScopePrefixes">
/// Optional path scope: when set, a node is a candidate (seed or hop target) only when its doc path starts with one of these
/// prefixes. It only narrows what <c>IVaultAccess</c> already allows; it never widens it.
/// </param>
public sealed record MemoryGraphOptions(
    int MaxHops = 1,
    int TopK = 20,
    int RrfK = 60,
    bool IncludeHistory = false,
    IReadOnlyList<string>? ScopePrefixes = null);

/// <summary>
/// One Constellation node returned by a retrieval, with the score it was packed under. The node is a Vault doc; when the doc is
/// a memory note, the memory fields come from its active revision.
/// </summary>
/// <param name="DocId">The Vault doc id (<c>vault_docs.doc_id</c>, "D" format). This is the node id.</param>
/// <param name="NoteId">The memory note id when the doc is a memory note, otherwise null.</param>
/// <param name="VaultPath">The vault path the caller may read.</param>
/// <param name="Title">The doc title.</param>
/// <param name="ScopeAgentId">The agent scope of a memory note, otherwise null.</param>
/// <param name="Kind">The <c>MemoryKind</c> name for a memory note, otherwise the Vault doc type (for example <c>decision</c>).</param>
/// <param name="Trust">The <c>MemoryTier</c> name for a memory note, otherwise null.</param>
/// <param name="Score">The score after expansion weighting.</param>
/// <param name="HopDistance">0 for a seed, 1 or 2 for an expansion hit.</param>
/// <param name="IsHistory">
/// True when this doc is superseded by a current head the caller can read. A history hit is never current advice and is never
/// ranked above its head; it is returned only when <see cref="MemoryGraphOptions.IncludeHistory"/> is set.
/// </param>
/// <param name="SupersededBy">For a history hit, the doc id ("D" format) of the current head of its supersedes chain; otherwise null.</param>
/// <param name="Version">The active memory revision version for a memory note, otherwise null.</param>
/// <param name="ViaDocId">For an expansion hit, the doc id of the parent it was reached from; for a head returned in place of a superseded seed, the superseded doc id; otherwise null.</param>
public sealed record MemoryGraphHit(
    string DocId,
    string? NoteId,
    string VaultPath,
    string Title,
    string? ScopeAgentId,
    string Kind,
    string? Trust,
    double Score,
    int HopDistance,
    bool IsHistory = false,
    string? SupersededBy = null,
    long? Version = null,
    string? ViaDocId = null);

/// <summary>
/// A retrieval result. <paramref name="LexicalOnly"/> is true when no vector seeds were used; <paramref name="LexicalOnlyReason"/>
/// then says why when the caller knows (a reason code of docs/design/constellation-memory-graph.md, "Vector seeds", rule 7).
/// </summary>
/// <param name="Hits">The packed hits, best first.</param>
/// <param name="LexicalOnly">True when the seeds came from lexical search only.</param>
/// <param name="LexicalOnlyReason">The reason code when <paramref name="LexicalOnly"/> is true and known, otherwise null.</param>
public sealed record MemoryGraphRetrieval(IReadOnlyList<MemoryGraphHit> Hits, bool LexicalOnly, string? LexicalOnlyReason = null);

/// <summary>
/// An expanding edge between two Constellation doc nodes, as the retriever follows it: a resolved Vault link, or a memory
/// <see cref="MemoryEdgeKind.DerivedFrom"/> or <see cref="MemoryEdgeKind.Supersedes"/> edge mapped to its doc.
/// </summary>
/// <param name="TargetDocId">The target doc id.</param>
/// <param name="Kind">The edge kind (<see cref="MemoryEdgeKind.Link"/>, <see cref="MemoryEdgeKind.DerivedFrom"/> or <see cref="MemoryEdgeKind.Supersedes"/>).</param>
/// <param name="Weight">The expansion weight in [0, 1].</param>
public sealed record ConstellationGraphEdge(Guid TargetDocId, MemoryEdgeKind Kind, double Weight);

/// <summary>
/// One Constellation node for retrieval (docs/design/constellation-memory-graph.md, "Convergence design"): a Vault doc, with
/// the active memory revision when the doc is a memory note.
/// </summary>
/// <param name="DocId">The Vault doc id.</param>
/// <param name="VaultPath">The doc path in the Vault index.</param>
/// <param name="Title">The doc title.</param>
/// <param name="DocType">The Vault doc type (<c>vault_docs.type</c>).</param>
/// <param name="FrontMatterStatus">The front matter <c>status</c>, or null.</param>
/// <param name="Memory">The active memory revision of this doc, or null when the doc is not a memory note.</param>
/// <param name="Edges">The expanding edges, already mapped to doc ids, deduplicated by target and kind.</param>
/// <param name="SupersededBy">
/// The docs whose active memory revision has a <see cref="MemoryEdgeKind.Supersedes"/> edge to this doc (incoming), sorted. Empty
/// when nothing supersedes it. The retriever still applies access, status and scope to each before it counts as a head.
/// </param>
public sealed record ConstellationGraphNode(
    Guid DocId,
    string VaultPath,
    string Title,
    string DocType,
    string? FrontMatterStatus,
    MemoryGraphRevision? Memory,
    IReadOnlyList<ConstellationGraphEdge> Edges,
    IReadOnlyList<Guid>? SupersededBy = null);
