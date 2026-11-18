using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// The replay projector (docs/design/26, section 4). Reads the GC log through <see cref="IGcLogReader"/>, so the viewer filter of
/// doc 01 applies to every entry, and folds events up to the effective seq. It writes nothing and calls no model.
/// </summary>
/// <remarks>
/// The effective seq is the requested seq clamped to the viewer's grant (<see cref="IGcReadAccess"/>) and to the GC's head.
/// Parts owned by other modules come from optional <see cref="IReplayFrameSource{T}"/> seams; a missing or failing source gives
/// an unavailable part, never a failed frame. Quarantined versions are flagged, and dropped for agent viewers
/// (quarantined entries are not delivered to agents, doc 01).
/// </remarks>
public sealed class ReplayProjector(
    IGcLogReader reader,
    IGcReadAccess access,
    IKanbanReplaySource? kanban = null,
    IVaultReplaySource? vault = null,
    ISoulReplaySource? souls = null,
    IReputationReplaySource? reputation = null) : IReplayProjector
{
    private const int _pageSize = 1_000;

    /// <inheritdoc />
    public async Task<ReplayFrame> ProjectAsync(ReplayCursor cursor, GcPrincipal viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        var grant = await access.GetGrantAsync(cursor.GcId, viewer, ct).ConfigureAwait(false);
        if (!grant.Allowed)
            throw new GcAccessDeniedException(cursor.GcId);

        var head = await reader.GetHeadSeqAsync(cursor.GcId, ct).ConfigureAwait(false);
        var effective = Math.Max(0, Math.Min(cursor.Seq, head));
        if (grant.ThroughSeq is { } through)
            effective = Math.Min(effective, Math.Max(0, through));

        var messages = await FoldAsync(cursor.GcId, effective, viewer, ct).ConfigureAwait(false);

        return new ReplayFrame(
            cursor,
            effective,
            Clamped: effective < cursor.Seq,
            messages,
            await LoadAsync(kanban, "Kanban module not installed", cursor.GcId, effective, viewer, ct).ConfigureAwait(false),
            await LoadAsync(vault, "Vault module not installed", cursor.GcId, effective, viewer, ct).ConfigureAwait(false),
            await LoadAsync(souls, "Soul versions not installed", cursor.GcId, effective, viewer, ct).ConfigureAwait(false),
            await LoadAsync(reputation, "Reputation not installed", cursor.GcId, effective, viewer, ct).ConfigureAwait(false));
    }

    private async Task<List<ReplayMessage>> FoldAsync(Guid gcId, long effective, GcPrincipal viewer, CancellationToken ct)
    {
        var latest = new Dictionary<Guid, (GcEvent Event, long FirstSeq)>();
        var quarantined = new HashSet<long>();
        var after = 0L;

        var stop = false;
        while (!stop && after < effective)
        {
            var limit = (int)Math.Min(_pageSize, effective - after);
            var read = 0;
            await foreach (var e in reader.ReadAsync(gcId, after, limit, viewer, ct).ConfigureAwait(false))
            {
                // Defensive: a reader that returns past the cursor must not leak later history into the frame.
                if (e.Seq > effective)
                {
                    stop = true;
                    break;
                }

                after = e.Seq;
                read++;

                if (e.Kind == GcEventKinds.Quarantine && e.TargetSeq is { } target)
                {
                    quarantined.Add(target);
                }
                else if ((e.Kind == GcEventKinds.Message || e.Kind == GcEventKinds.MessageEdit) && e.MessageId is { } id)
                {
                    var first = latest.TryGetValue(id, out var prior) ? prior.FirstSeq : e.Seq;
                    if (!latest.TryGetValue(id, out prior) || (e.Version ?? 1) >= (prior.Event.Version ?? 1))
                        latest[id] = (e, first);
                }
            }

            if (read == 0)
                break;
        }

        var result = new List<ReplayMessage>(latest.Count);
        foreach (var (id, (e, firstSeq)) in latest.OrderBy(kv => kv.Value.FirstSeq))
        {
            var isQuarantined = quarantined.Contains(e.Seq);
            if (isQuarantined && viewer.Kind == PrincipalKind.Agent)
                continue;
            var version = e.Version ?? 1;
            result.Add(new ReplayMessage(id, e.AuthorId, firstSeq, e.Seq, version, version > 1, isQuarantined, e.PayloadJson, e.CreatedAt));
        }

        return result;
    }

    private static async Task<ReplayPart<T>> LoadAsync<T>(
        IReplayFrameSource<T>? source, string missingReason, Guid gcId, long seq, GcPrincipal viewer, CancellationToken ct)
    {
        if (source is null)
            return ReplayPart.Unavailable<T>(missingReason);
        try
        {
            return ReplayPart.Available(await source.GetAsync(gcId, seq, viewer, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Degrade, do not fail the frame. The reason names no detail: source errors may carry data the viewer may not see.
            return ReplayPart.Unavailable<T>("Source failed");
        }
    }
}
