using Bazaar.Erasure.Abstractions;

namespace Bazaar.Erasure;

/// <summary>The durable side of <see cref="LegalHoldService"/>: append-only events plus the audit row, in one step.</summary>
public interface ILegalHoldStore
{
    /// <summary>Appends a 'created' event and its audit entry together.</summary>
    Task CreateAsync(Guid holdId, LegalHoldScopeKind kind, string scopeValue, string reason, string actor, string signatureKeyId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Appends a 'released' event and its audit entry together, only while the hold is active. Returns false, and appends
    /// nothing, when it is not active. Two racing releases of one hold append one event.
    /// </summary>
    Task<bool> ReleaseAsync(Guid holdId, string reason, string actor, string signatureKeyId, DateTimeOffset at, CancellationToken ct);

    Task<IReadOnlyList<LegalHold>> ListActiveAsync(CancellationToken ct);
}
