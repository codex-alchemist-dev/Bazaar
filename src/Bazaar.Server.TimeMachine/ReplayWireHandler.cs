using Bazaar.Contracts.Gc;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// The outcome of one replay wire request: a value, or a wire error code from <see cref="WireErrorCodes"/>. The wire host adapts
/// this to its own reply type, so this module needs no reference to Bazaar.Server.
/// </summary>
/// <typeparam name="T">The result payload.</typeparam>
/// <param name="Value">The payload, or default on an error.</param>
/// <param name="ErrorCode">The wire error code, or null on success.</param>
/// <param name="Message">A safe message for the error, or null.</param>
public sealed record ReplayWireReply<T>(T? Value, string? ErrorCode, string? Message)
{
    /// <summary>True when <see cref="Value"/> holds the answer.</summary>
    public bool IsOk => ErrorCode is null;

    internal static ReplayWireReply<T> Ok(T value) => new(value, null, null);

    internal static ReplayWireReply<T> Fail(string code, string? message = null) => new(default, code, message);
}

/// <summary>
/// Maps <c>replay.frame</c> and <c>replay.fork</c> (doc 02, replay and fork requests) onto <see cref="IReplayProjector"/> and
/// <see cref="IForkService"/>. Both are read-only on the log. A GC the viewer cannot read gets <see cref="WireErrorCodes.NotFound"/>,
/// the same as <c>gc.history</c>, so membership does not leak. The wire host registers an adapter that passes the connection's
/// principal in and the reply out; the rate bucket (<c>gc.history</c>) is the host's.
/// </summary>
public sealed class ReplayWireHandler(IReplayProjector projector, IForkService forks, IGcReadAccess access, IGcLogReader reader)
{
    /// <summary>Answers <c>replay.frame</c>. The result carries the clamped seq and the viewer's visible head, never the true head.</summary>
    public async Task<ReplayWireReply<ReplayFrameResult>> FrameAsync(GcPrincipal viewer, ReplayFrameRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Gc == Guid.Empty || request.Seq < 0)
        {
            return ReplayWireReply<ReplayFrameResult>.Fail(WireErrorCodes.BadRequest, "A replay frame request needs a GC and a seq of zero or more.");
        }

        try
        {
            var frame = await projector.ProjectAsync(new ReplayCursor(request.Gc, request.Seq), viewer, ct).ConfigureAwait(false);
            var grant = await access.GetGrantAsync(request.Gc, viewer, ct).ConfigureAwait(false);
            var head = await reader.GetHeadSeqAsync(request.Gc, ct).ConfigureAwait(false);
            if (grant.ThroughSeq is { } through)
            {
                head = Math.Min(head, Math.Max(0, through));
            }

            return ReplayWireReply<ReplayFrameResult>.Ok(new ReplayFrameResult(request.Gc, frame.EffectiveSeq, head, Map(frame)));
        }
        catch (GcAccessDeniedException)
        {
            return ReplayWireReply<ReplayFrameResult>.Fail(WireErrorCodes.NotFound);
        }
    }

    /// <summary>Answers <c>replay.fork</c>. A repeat on (gc, effective seq, request id) returns the first result with <c>created: false</c>.</summary>
    public async Task<ReplayWireReply<ReplayForkResult>> ForkAsync(GcPrincipal viewer, ReplayForkRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Gc == Guid.Empty || request.Seq < 0 || request.RequestId == Guid.Empty)
        {
            return ReplayWireReply<ReplayForkResult>.Fail(WireErrorCodes.BadRequest, "A replay fork request needs a GC, a seq of zero or more, and a request id.");
        }

        try
        {
            var result = await forks.ForkAsync(new ForkRequest(request.RequestId, request.Gc, request.Seq, viewer), ct).ConfigureAwait(false);
            return ReplayWireReply<ReplayForkResult>.Ok(new ReplayForkResult(result.Link.NewGcId, result.Link.VaultCommit, result.Created));
        }
        catch (GcAccessDeniedException)
        {
            return ReplayWireReply<ReplayForkResult>.Fail(WireErrorCodes.NotFound);
        }
    }

    private static ReplayFrameWire Map(ReplayFrame frame) => new(
        [.. frame.Messages.Select(m => new ReplayMessageWire(
            m.MessageId, m.AuthorId, m.FirstSeq, m.VersionSeq, m.Version, m.Edited, m.Quarantined, m.PayloadJson, m.CreatedAt))],
        MapPart(frame.Kanban, (a, v, r) => new ReplayTasksPart(a, v, r), v => (IReadOnlyList<ReplayTaskWire>)[.. v.Select(t => new ReplayTaskWire(t.TaskId, t.Title, t.Column))]),
        MapPart(frame.Vault, (a, v, r) => new ReplayVaultPart(a, v, r), v => new ReplayVaultWire(v.Commit)),
        MapPart(frame.Souls, (a, v, r) => new ReplaySoulsPart(a, v, r), v => (IReadOnlyList<ReplaySoulWire>)[.. v.Select(s => new ReplaySoulWire(s.AgentId, s.Version))]),
        MapPart(frame.Reputation, (a, v, r) => new ReplayReputationPart(a, v, r), v => (IReadOnlyList<ReplayReputationWire>)[.. v.Select(r => new ReplayReputationWire(r.AgentId, r.Score))]));

    private static TPart MapPart<TSource, TWire, TPart>(ReplayPart<TSource> part, Func<bool, TWire?, string?, TPart> make, Func<TSource, TWire> map) =>
        part.IsAvailable && part.Value is { } value
            ? make(true, map(value), null)
            : make(false, default, part.Reason ?? "unavailable");
}
