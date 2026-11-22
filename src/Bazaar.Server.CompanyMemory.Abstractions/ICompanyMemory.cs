namespace Bazaar.Server.CompanyMemory;

/// <summary>Company Memory (docs/design/25-company-systems-knowledge.md, docs/design/08-memory-system.md section 6).</summary>
public interface ICompanyMemory
{
    /// <summary>
    /// Records a decision in the Vault. Every superseded decision must exist. Throws <see cref="ArgumentException"/> for a
    /// missing or self-referencing target or a field the Vault refuses.
    /// </summary>
    Task<RecordedDecision> RecordDecisionAsync(RecordDecisionRequest request, CancellationToken ct);

    /// <summary>
    /// Returns the chain around a decision named by number (<c>0002</c>), name (<c>0002-slug</c>) or path, or null when
    /// no such decision exists.
    /// </summary>
    Task<DecisionChainView?> GetChainAsync(string reference, CancellationToken ct);

    /// <summary>Rebuilds the <see cref="IDecisionLinkIndex"/> from the decisions in the store. Returns the number of edges.</summary>
    Task<int> RebuildIndexAsync(CancellationToken ct);
}

/// <summary>
/// Where decisions live. The Vault adapter reads and writes Markdown in git; tests use an in-memory one. The store holds the
/// truth, so the chain is always rebuilt from <see cref="ListAsync"/>.
/// </summary>
public interface ICompanyDecisionStore
{
    /// <summary>Every decision record, in any order.</summary>
    Task<IReadOnlyList<Bazaar.Server.Knowledge.CompanyMemory.DecisionRecord>> ListAsync(CancellationToken ct);

    /// <summary>Writes a new decision with the next free number. Idempotent by <see cref="RecordDecisionRequest.DocId"/>.</summary>
    Task<RecordedDecision> CreateAsync(RecordDecisionRequest request, CancellationToken ct);
}

/// <summary>
/// The derived <c>decision_links</c> index (doc 25): chain edges and cached status, rebuilt from the Vault and never
/// authoritative. Other modules (Archive, Constellation) read edges here without parsing every decision file.
/// </summary>
public interface IDecisionLinkIndex
{
    /// <summary>Replaces every edge with <paramref name="links"/>, atomically. An empty list clears the index.</summary>
    Task ReplaceAllAsync(IReadOnlyList<Bazaar.Server.Knowledge.CompanyMemory.DecisionLink> links, CancellationToken ct);

    /// <summary>The edges that start or end at <paramref name="path"/> (a Vault path), sorted by from, then to.</summary>
    Task<IReadOnlyList<Bazaar.Server.Knowledge.CompanyMemory.DecisionLink>> LinksTouchingAsync(string path, CancellationToken ct);
}
