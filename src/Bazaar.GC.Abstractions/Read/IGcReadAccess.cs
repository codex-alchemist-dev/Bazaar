using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Read;

/// <summary>What a viewer may read in one GC.</summary>
/// <param name="Allowed">Whether the viewer may read at all.</param>
/// <param name="ThroughSeq">Last seq the viewer may see, or null for no limit (a member who left keeps what they saw, not later events).</param>
public readonly record struct GcReadGrant(bool Allowed, long? ThroughSeq)
{
    public static GcReadGrant Denied { get; } = new(false, null);

    public static GcReadGrant Full { get; } = new(true, null);

    public static GcReadGrant Through(long seq) => new(true, seq);
}

/// <summary>
/// The one place viewer rules live: membership, DM and Thinking GC visibility, left_seq (doc 01 WP7). The reader asks it before
/// every read. There is deliberately no default implementation, so a host that forgets to register one fails at start rather
/// than reading everything.
/// </summary>
public interface IGcReadAccess
{
    ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct);
}
