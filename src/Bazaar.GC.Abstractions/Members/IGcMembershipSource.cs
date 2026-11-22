using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Members;

/// <summary>What a membership plan needs: the header, the head seq the plan is made against, and every current membership row.</summary>
/// <param name="Gc">The GC header.</param>
/// <param name="HeadSeq">The head seq read with the header. The writer rejects the write when the head has moved.</param>
/// <param name="Members">The current membership rows, one per principal.</param>
public sealed record GcMembershipSnapshot(GcRecord Gc, long HeadSeq, IReadOnlyCollection<GcMemberRecord> Members);

/// <summary>Reads the state a membership change is planned against. Read only; the writer does the append.</summary>
public interface IGcMembershipSource
{
    /// <summary>Loads the snapshot, or null when the GC does not exist.</summary>
    Task<GcMembershipSnapshot?> LoadAsync(Guid gcId, CancellationToken ct);
}
