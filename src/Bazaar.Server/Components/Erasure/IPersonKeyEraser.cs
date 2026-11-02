namespace Bazaar.Server.Components.Erasure;

/// <summary>
/// Destroys the GC per-person keys of one person (migration 0008, <c>person_key</c>). Destroying a key makes every sealed
/// body that key covers unreadable; the tombstone versions are appended by the erasure flow, not here.
/// </summary>
public interface IPersonKeyEraser
{
    /// <summary>Destroys every key the person still holds and reports how many were destroyed now or were already gone.</summary>
    Task<PersonKeyEraseResult> EraseAsync(Guid personId, CancellationToken ct = default);
}

/// <summary>
/// Counts from one run. Both zero means no key row exists for the person.
/// </summary>
public sealed record PersonKeyEraseResult(int Destroyed, int AlreadyDestroyed);
