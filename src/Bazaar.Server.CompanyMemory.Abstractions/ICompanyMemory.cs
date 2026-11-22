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
