using Bazaar.Contracts.Gc;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// The wire's <see cref="IGcAccessPolicy"/>, answered by the one set of viewer rules (<see cref="IGcReadAccess"/>).
/// Only a full grant opens a stream. A former member has a limited grant and so loses the stream, which is stricter than
/// replay-to-leave-seq: they keep what they saw in the log, but a live socket is not a read path for them.
/// </summary>
/// <remarks>
/// The viewer's kind comes from the authenticated session (<c>SubscriptionEngine.CreateSession</c>), so an owner-kind
/// session reads every GC (docs/design/01). Until doc 14 supplies kinds, sessions default to human, which fails closed.
/// </remarks>
public sealed class GcStreamAccessPolicy(IGcReadAccess access) : IGcAccessPolicy
{
    /// <inheritdoc />
    public async ValueTask<bool> CanReadAsync(GcPrincipal viewer, Guid gcId, CancellationToken ct)
    {
        var grant = await access.GetGrantAsync(gcId, viewer, ct)
            .ConfigureAwait(false);
        return grant is { Allowed: true, ThroughSeq: null };
    }
}
