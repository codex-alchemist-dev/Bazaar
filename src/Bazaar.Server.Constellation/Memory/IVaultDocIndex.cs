namespace Bazaar.Server.Constellation.Memory;

/// <summary>A Vault index doc as the memory projection needs it.</summary>
/// <param name="DocId">The doc id (<c>vault_docs.doc_id</c>).</param>
/// <param name="FrontMatterId">The front matter <c>id</c>, or null.</param>
/// <param name="LinkTargets">The doc ids its wiki-links resolve to (<c>vault_links.target_doc</c>), distinct, in a stable order.</param>
public sealed record VaultIndexedDoc(Guid DocId, string? FrontMatterId, IReadOnlyList<Guid> LinkTargets);

/// <summary>
/// Looks a memory note's file up in the Vault index (docs/design/constellation-memory-graph.md, "Convergence design"), so the
/// projector uses the Vault doc id and the links the index already resolved instead of parsing links again.
/// </summary>
public interface IVaultDocIndex
{
    /// <summary>
    /// The doc at <paramref name="vaultPath"/> for note <paramref name="noteId"/>, or null when the index has no doc there, the
    /// path is ambiguous, or the doc's front matter id names another note (<see cref="MemoryDocIdResolution"/>).
    /// </summary>
    Task<VaultIndexedDoc?> FindAsync(string noteId, string vaultPath, CancellationToken ct);
}
