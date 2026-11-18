using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// Forks a GC at a seq (docs/design/26, section 4). The requester must be able to read the source, and a seq above their grant is
/// clamped to it. Writes one append-only <see cref="ForkLink"/> and copies no messages. Repeating a request returns the first fork.
/// </summary>
public sealed class ForkService(
    IGcReadAccess access,
    IGcLogReader reader,
    IForkLinkStore store,
    IForkTargetProvisioner provisioner,
    TimeProvider? time = null) : IForkService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ForkResult> ForkAsync(ForkRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var grant = await access.GetGrantAsync(request.SourceGcId, request.Requester, ct).ConfigureAwait(false);
        if (!grant.Allowed)
            throw new GcAccessDeniedException(request.SourceGcId);

        var head = await reader.GetHeadSeqAsync(request.SourceGcId, ct).ConfigureAwait(false);
        var seq = Math.Max(0, Math.Min(request.SourceSeq, head));
        if (grant.ThroughSeq is { } through)
            seq = Math.Min(seq, Math.Max(0, through));

        var existing = await store.FindAsync(request.SourceGcId, seq, request.RequestId, ct).ConfigureAwait(false);
        if (existing is not null)
            return new ForkResult(existing, false);

        var target = await provisioner.ProvisionAsync(request.RequestId, request.SourceGcId, seq, request.Requester, ct).ConfigureAwait(false);
        var link = new ForkLink(target.NewGcId, request.SourceGcId, seq, target.VaultCommit, request.RequestId, _time.GetUtcNow());
        var (stored, created) = await store.AppendAsync(link, ct).ConfigureAwait(false);
        return new ForkResult(stored, created);
    }
}
