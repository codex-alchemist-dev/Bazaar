using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// Whether a principal may write in a GC: a member who can read it in full, in a GC that is not archived; a Thinking GC takes
/// writes only from its agent and the owner (docs/design/01, Membership). The one place the write rule lives for features that
/// are not messages (reactions, typing), so they never re-derive membership. Denied and missing GCs answer the same.
/// </summary>
public interface IGcWriteAccess
{
    /// <summary>True when <paramref name="writer"/> may append to the GC.</summary>
    ValueTask<bool> CanWriteAsync(Guid gcId, GcPrincipal writer, CancellationToken ct);
}
