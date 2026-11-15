namespace Bazaar.Server.Gc.Write;

/// <summary>
/// The GC's head moved between the read a plan was made from and the write. The plan's membership decision may no longer
/// hold, so nothing is written. The caller re-reads and plans again.
/// </summary>
public sealed class GcStaleHeadException(Guid gcId, long expectedHead, long actualHead)
    : Exception($"GC {gcId} head is {actualHead}; the plan expected {expectedHead}.")
{
    /// <summary>The GC whose head moved.</summary>
    public Guid GcId { get; } = gcId;

    /// <summary>The head the plan was made against.</summary>
    public long ExpectedHead { get; } = expectedHead;

    /// <summary>The head seen under the row lock.</summary>
    public long ActualHead { get; } = actualHead;
}
