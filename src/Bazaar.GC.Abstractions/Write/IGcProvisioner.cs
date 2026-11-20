namespace Bazaar.Server.Gc.Write;

/// <summary>
/// Creates GCs on behalf of other modules (doc 40 section 5a, step 5). A module that needs a new room, such as the Time
/// Machine's fork, composes this seam with its own links; the GC module never learns about those links.
/// </summary>
public interface IGcProvisioner
{
    /// <summary>
    /// Creates a project GC with the owner as its only member. It is idempotent on <paramref name="gcId"/>: a second call with the
    /// same id changes nothing and returns false. Derive the id from the caller's request id so a retry finds the same GC. The new
    /// GC has no parent and carries no link metadata; the caller records any link in its own store.
    /// </summary>
    /// <returns>True when this call created the GC, false when it already existed.</returns>
    Task<bool> CreateProjectGcAsync(Guid gcId, string title, Bazaar.Contracts.Gc.GcPrincipal owner, CancellationToken ct);
}
