using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Kodo;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Opens the <c>kodo</c> stream on the existing wire subscription engine (doc 46 section 15.2, PROPOSED). It registers as an
/// <see cref="IStreamGate"/> like the presence stream does, so Wire never references this module. A subscriber passes
/// <see cref="IKodoStreamAccess"/>, then gets the latest snapshot as its first event, and after that the revisioned
/// <c>state_changed</c> and <c>heartbeat</c> frames <see cref="KodoWireStreamPublisher"/> pushes. Subscribing again (the client's
/// recovery after a gap) sends a fresh snapshot. A switched-off module refuses everyone.
/// </summary>
public sealed class KodoStreamGate(IKodoStatusSource source, IKodoStreamAccess access, TimeProvider time) : IStreamGate
{
    /// <inheritdoc />
    public string Stream => WireStreams.Kodo;

    /// <inheritdoc />
    public async ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
        source.Enabled && await access.CanViewAsync(principalId, principalKind, ct).ConfigureAwait(false);

    /// <summary>
    /// The snapshot event for the current state. Its revision is the last delta revision the monitor numbered before it built the
    /// snapshot, so deltas published after it either repeat an older revision (the client ignores them) or continue by one. Before
    /// the first evaluation there is nothing to send; the first delta then makes the client ask again.
    /// </summary>
    public ValueTask<StreamOpening?> OpenAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct)
    {
        if (source.Latest is not { } snapshot)
        {
            return ValueTask.FromResult<StreamOpening?>(null);
        }

        var snapshotEvent = new KodoStreamEventDto(snapshot.Revision, KodoStreamEventDto.SnapshotType, time.GetUtcNow(), null, snapshot);
        return ValueTask.FromResult<StreamOpening?>(new StreamOpening(WireTypes.KodoEvent, KodoWireCodec.EncodeEvent(snapshotEvent)));
    }
}
