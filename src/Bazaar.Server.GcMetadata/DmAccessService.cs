using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.GcMetadata;

/// <summary>
/// The DM access flow (docs/design/01-gc-data-model.md, DM owner-read flow; docs/design/32, Per-GC). The owner reads every DM; others
/// request access; each request, decision, refusal and owner read is audited.
/// </summary>
public sealed class DmAccessService(
    IDmDirectory directory,
    IDmAccessLedger ledger,
    IDmAccessEvents events,
    IAuditLog audit,
    DmAccessOptions? options = null,
    TimeProvider? time = null) : IDmAccessService, IDmReadAuthorizer
{
    private readonly DmAccessOptions _options = options ?? new DmAccessOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<DmAccessResult> RequestAsync(DmActor requester, Guid gcId, CancellationToken ct)
    {
        if (requester.Id == Guid.Empty)
            throw new ArgumentException("The requester id must not be empty.", nameof(requester));

        var dm = await directory.FindAsync(gcId, ct).ConfigureAwait(false);
        if (dm is null)
            return new DmAccessResult(DmAccessOutcome.NotADm, null);

        if (requester.Kind == PrincipalKind.Owner || dm.ActiveMembers.Contains(requester.Id))
            return new DmAccessResult(DmAccessOutcome.AlreadyAllowed, null);

        var existing = (await ledger.ListAsync(gcId, ct).ConfigureAwait(false)).Where(r => r.Requester == requester.Id).ToList();
        if (existing.FirstOrDefault(r => r.Status == DmAccessStatus.Approved) is { } approved)
            return new DmAccessResult(DmAccessOutcome.AlreadyAllowed, approved);
        if (existing.FirstOrDefault(r => r.Status == DmAccessStatus.Pending) is { } pending)
            return new DmAccessResult(DmAccessOutcome.AlreadyPending, pending);

        var now = _time.GetUtcNow();
        var request = await ledger.CreateAsync(gcId, requester.Id, now, ct).ConfigureAwait(false);
        await events.AppendAsync(gcId, "access_requested", request.Id, requester.Id, requester.Id, ct).ConfigureAwait(false);
        await WriteAuditAsync(now, requester, "dm.access.requested", gcId, request.Id, requester.Id, ct).ConfigureAwait(false);
        return new DmAccessResult(DmAccessOutcome.Requested, request);
    }

    /// <inheritdoc />
    public async Task<DmAccessResult> DecideAsync(DmActor decider, Guid requestId, bool approve, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var request = await ledger.GetAsync(requestId, ct).ConfigureAwait(false);
        if (request is null || request.Status != DmAccessStatus.Pending)
            return new DmAccessResult(DmAccessOutcome.NotPending, request);

        var dm = await directory.FindAsync(request.GcId, ct).ConfigureAwait(false);
        if (dm is null)
            return new DmAccessResult(DmAccessOutcome.NotADm, request);

        if (!MayDecide(decider, dm, request))
        {
            await WriteAuditAsync(now, decider, "dm.access.refused", request.GcId, request.Id, request.Requester, ct).ConfigureAwait(false);
            return new DmAccessResult(DmAccessOutcome.NotAllowed, request);
        }

        var status = approve ? DmAccessStatus.Approved : DmAccessStatus.Denied;
        if (!await ledger.ResolveAsync(requestId, status, decider.Id, now, ct).ConfigureAwait(false))
            return new DmAccessResult(DmAccessOutcome.NotPending, request);

        await events.AppendAsync(request.GcId, approve ? "access_granted" : "access_denied", request.Id, request.Requester, decider.Id, ct).ConfigureAwait(false);
        await WriteAuditAsync(now, decider, approve ? "dm.access.granted" : "dm.access.denied", request.GcId, request.Id, request.Requester, ct).ConfigureAwait(false);
        return new DmAccessResult(approve ? DmAccessOutcome.Granted : DmAccessOutcome.Denied, request with { Status = status, DecidedBy = decider.Id });
    }

    /// <inheritdoc />
    public async Task<DmReadDecision> AuthorizeReadAsync(DmActor viewer, Guid gcId, CancellationToken ct)
    {
        var dm = await directory.FindAsync(gcId, ct).ConfigureAwait(false);
        if (dm is null)
            return new DmReadDecision(false, DmReadBasis.None, false);

        if (dm.ActiveMembers.Contains(viewer.Id))
            return new DmReadDecision(true, DmReadBasis.Member, false);

        if (viewer.Kind == PrincipalKind.Owner)
        {
            await WriteAuditAsync(_time.GetUtcNow(), viewer, "dm.owner_read", gcId, null, null, ct).ConfigureAwait(false);
            return new DmReadDecision(true, DmReadBasis.Owner, _options.ShowOwnerReadMarker);
        }

        var granted = (await ledger.ListAsync(gcId, ct).ConfigureAwait(false))
            .Any(r => r.Requester == viewer.Id && r.Status == DmAccessStatus.Approved);
        return granted ? new DmReadDecision(true, DmReadBasis.Grant, false) : new DmReadDecision(false, DmReadBasis.None, false);
    }

    // The owner always decides. A member decides only when the setting allows it, never for their own request. An agent never decides.
    private bool MayDecide(DmActor decider, DmInfo dm, DmAccessRequest request)
    {
        if (decider.Kind == PrincipalKind.Agent)
            return false;
        if (decider.Kind == PrincipalKind.Owner)
            return true;
        return _options.Approvers == DmApprovers.OwnerOrDmMember
            && decider.Id != request.Requester
            && dm.ActiveMembers.Contains(decider.Id);
    }

    private Task WriteAuditAsync(DateTimeOffset at, DmActor actor, string kind, Guid gcId, Guid? requestId, Guid? requester, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["gc_id"] = gcId.ToString("D"),
            ["actor_kind"] = actor.Kind.ToString().ToLowerInvariant(),
        };
        if (requestId is { } r)
            payload["request_id"] = r.ToString("D");
        if (requester is { } q)
            payload["requester"] = q.ToString("D");
        return audit.AppendAsync(new AuditEntry(at, actor.Id.ToString("D"), kind, payload.ToJsonString()), ct);
    }
}
