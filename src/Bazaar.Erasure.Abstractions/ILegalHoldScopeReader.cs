namespace Bazaar.Erasure.Abstractions;

/// <summary>
/// Answers whether any non-person legal hold (scope <c>gc</c>, <c>company</c> or <c>category</c>) is active. How such a hold covers
/// a given table is OPEN (doc 48 section 9), so retention sweeps treat any active non-person hold as blocking the whole category
/// (coordinator decision, 2026-11-18; most protective). Erasure of a person still uses <see cref="ILegalHoldReader"/> only.
/// </summary>
public interface ILegalHoldScopeReader
{
    /// <summary>True when at least one active hold has a scope other than <c>person</c>.</summary>
    Task<bool> HasActiveNonPersonHoldAsync(CancellationToken ct = default);
}
