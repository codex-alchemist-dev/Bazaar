using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// What a writer may do in one GC, and who is in it (doc 01 WP7). The write side of <see cref="IGcReadAccess"/>: wire handlers ask
/// it before appending, so the rules stay in one place. There is deliberately no default implementation.
/// </summary>
public interface IGcWriteAccess
{
    /// <summary>
    /// True when the writer may append to the GC: readable in full and not blocked by the GC's rules (archived GCs are read-only
    /// for everyone; Thinking GCs take writes only from their owning agent and the owner).
    /// </summary>
    ValueTask<bool> CanWriteAsync(Guid gcId, GcPrincipal writer, CancellationToken ct);

    /// <summary>The principals with an active membership row (no <c>left_seq</c>), for mention checks.</summary>
    Task<HashSet<Guid>> LoadActiveMemberIdsAsync(Guid gcId, CancellationToken ct);
}
