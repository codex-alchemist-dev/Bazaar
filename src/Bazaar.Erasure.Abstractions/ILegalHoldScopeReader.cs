namespace Bazaar.Erasure.Abstractions;

/// <summary>One active legal hold (design doc 48 section 9): a scope kind (<c>person</c>, <c>gc</c>, <c>company</c> or <c>category</c>) and the value it names.</summary>
public sealed record ActiveLegalHold(Guid HoldId, string ScopeKind, string ScopeValue);

/// <summary>
/// Lists every active legal hold, whatever its scope. The retention sweeper reads this before each destructive step and matches
/// the holds against a record itself, so a new scope kind fails closed instead of being skipped. A separate interface from
/// <see cref="ILegalHoldReader"/> so existing implementers of that one do not break.
/// </summary>
public interface ILegalHoldScopeReader
{
    /// <summary>All holds that are active right now. Throws when the hold store cannot be read; callers must then not delete.</summary>
    Task<IReadOnlyList<ActiveLegalHold>> ListActiveAsync(CancellationToken ct = default);
}
