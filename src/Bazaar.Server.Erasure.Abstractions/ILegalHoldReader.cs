namespace Bazaar.Server.Erasure.Abstractions;

/// <summary>
/// Answers whether a person is covered by an active legal hold (design doc 48 section 9). Erasure checks this before it
/// destroys any key. Only holds with scope <c>person</c> are read today; <c>gc</c>, <c>company</c> and <c>category</c> holds
/// are recorded but not enforced yet (OPEN, owner decision needed on how they cover a person).
/// </summary>
public interface ILegalHoldReader
{
    /// <summary>True when an active hold with scope <c>person</c> names this person.</summary>
    Task<bool> IsPersonHeldAsync(Guid personId, CancellationToken ct = default);
}
