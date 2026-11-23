namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Storage for memory graph revisions (docs/design/constellation-memory-graph.md, "Store"). Revisions are append-only:
/// a revision is never updated or deleted, and rollback only changes which stored revision is active.
/// </summary>
public interface IMemoryGraphStore
{
    /// <summary>
    /// Stores a revision and makes it the active one for its note, in one transaction. The previous active revision stays
    /// stored and inactive. Storing a (note, version) pair that already exists is an error.
    /// </summary>
    Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct);

    /// <summary>
    /// Makes a stored earlier revision the active one (the rollback). Returns false when that version does not exist.
    /// Nothing is deleted.
    /// </summary>
    Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct);

    /// <summary>The active revisions of the given note ids. Ids with no active revision are left out.</summary>
    Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct);

    /// <summary>Every stored revision of one note, newest version first.</summary>
    Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct);

    /// <summary>
    /// Every stored revision of the memory note that is Vault doc <paramref name="docId"/>, newest version first. History is
    /// keyed by the doc node (docs/design/constellation-memory-graph.md, "Convergence design"). Empty when no revision is mapped.
    /// </summary>
    Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsByDocAsync(Guid docId, CancellationToken ct);
}
