using Bazaar.Contracts.Gc;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Gc.Read;

/// <summary>
/// The wire's <see cref="IGcAccessPolicy"/>, answered by the one set of viewer rules (<see cref="IGcReadAccess"/>).
/// Only a full grant opens a stream. A former member has a limited grant and so loses the stream, which is stricter than
/// replay-to-leave-seq: they keep what they saw in the log, but a live socket is not a read path for them.
/// </summary>
/// <remarks>
/// The principal kind travels with the id, so the owner shortcut (docs/design/01: the owner reads all, DMs included) applies
/// on the live stream exactly as it does on replay.
/// </remarks>
public sealed class GcStreamAccessPolicy(IGcReadAccess access) : IGcAccessPolicy
{
    /// <inheritdoc />
    public async ValueTask<bool> CanReadAsync(Guid principalId, GcParticipantKind kind, Guid gcId, CancellationToken ct)
    {
        var grant = await access.GetGrantAsync(gcId, new GcPrincipal(principalId, kind, string.Empty), ct)
            .ConfigureAwait(false);
        return grant is { Allowed: true, ThroughSeq: null };
    }
}
