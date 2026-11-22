using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Read;

/// <summary>What a viewer may read in one GC.</summary>
/// <param name="Allowed">Whether the viewer may read at all.</param>
/// <param name="ThroughSeq">Last seq the viewer may see, or null for no limit (a member who left keeps what they saw, not later events).</param>
/// <param name="FromSeq">First seq the viewer may see, or null for no lower limit. A DM access grant reads forward from its grant seq only (doc 32).</param>
public readonly record struct GcReadGrant(bool Allowed, long? ThroughSeq, long? FromSeq = null)
{
    /// <summary>True when the viewer may read every event: allowed, with no lower and no upper limit.</summary>
    public bool IsFull => Allowed && ThroughSeq is null && FromSeq is null;

    /// <summary>True when <paramref name="seq"/> lies inside the window the grant allows.</summary>
    public bool Covers(long seq) => Allowed && (FromSeq is null || seq >= FromSeq) && (ThroughSeq is null || seq <= ThroughSeq);

    /// <summary>A grant that reads from <paramref name="seq"/> onward.</summary>
    public static GcReadGrant From(long seq) => new(true, null, seq);

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
