namespace Bazaar.Server.Gc.Write;

/// <summary>
/// Creates and finds GCs on behalf of other modules (doc 40 section 5a, step 5). A module that needs a room, such as the Time
/// Machine's fork or a mail inbox, composes this seam with its own links; the GC module never learns about those links.
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

    /// <summary>
    /// Creates a GC of the given seeded type with the owner as its only member and the given string metadata. Idempotent on
    /// <paramref name="gcId"/> like <see cref="CreateProjectGcAsync"/>. An unknown <paramref name="typeKey"/> is refused.
    /// </summary>
    /// <returns>True when this call created the GC, false when it already existed.</returns>
    Task<bool> CreateAsync(
        Guid gcId,
        string typeKey,
        string title,
        Bazaar.Contracts.Gc.GcPrincipal owner,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct);

    /// <summary>
    /// Finds the id of the GC of this type created by <paramref name="ownerId"/> whose metadata has <paramref name="metadataKey"/>
    /// equal to <paramref name="metadataValue"/>. Null when there is none. When more than one matches, the oldest is returned.
    /// </summary>
    Task<Guid?> FindAsync(string typeKey, Guid ownerId, string metadataKey, string metadataValue, CancellationToken ct);

    /// <summary>
    /// Adds a member to a GC. Idempotent: a principal who is already a current member changes nothing and returns false.
    /// A principal who left is added again at the GC's current head.
    /// </summary>
    /// <returns>True when this call added the member.</returns>
    Task<bool> AddMemberAsync(Guid gcId, Bazaar.Contracts.Gc.GcPrincipal member, CancellationToken ct);

    /// <summary>
    /// Finds or creates the direct-message GC between two principals (doc 01: DMs are two-member GCs of type <c>dm</c>; the owner's
    /// read-all access is a read rule, not a membership). The id is derived from the unordered pair, so asking in either order, or
    /// twice, lands on the same GC. The title is blank-safe: the display names joined, trimmed to the title limit.
    /// </summary>
    /// <returns>The DM GC id.</returns>
    Task<Guid> EnsureDmAsync(Bazaar.Contracts.Gc.GcPrincipal first, Bazaar.Contracts.Gc.GcPrincipal second, CancellationToken ct);
}
