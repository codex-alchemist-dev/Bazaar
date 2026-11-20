using System.Text;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.ApprovalQueue;

/// <summary>
/// The approvals queue (docs/design/51-web-admin-module.md, approvals queue). Expiry is evaluated on read. A decision needs the
/// approval capability (<see cref="Capabilities.Approve"/>, the human approval capability that agents never hold) at instance
/// scope, a tier 2 item needs a human approver, and the requester never decides their own request. Every request, decision and
/// consume appends an audit entry (<c>approval.requested</c>, <c>approval.decided</c>, <c>approval.consumed</c>) with ids only.
/// </summary>
public sealed class ApprovalQueueService(
    IApprovalStore store,
    IPermissionChecker permissions,
    IRoleStore roles,
    IAuditLog audit,
    TimeProvider clock) : IApprovalQueue
{
    public static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxTtl = TimeSpan.FromDays(30);

    public async Task<Guid> RequestAsync(NewApproval request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.Subject))
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidRequest, "Kind and subject are required.");
        if (request.Tier is not (1 or 2))
            throw new ApprovalRefusedException(ApprovalRefusal.InvalidRequest, "Tier must be 1 or 2.");

        var now = clock.GetUtcNow();
        var ttl = request.Ttl < MinTtl ? MinTtl : request.Ttl > MaxTtl ? MaxTtl : request.Ttl;
        var item = new ApprovalItem(
            Guid.NewGuid(), request.Kind.Trim(), request.Tier, request.Subject.Trim(), request.RequesterPrincipalId,
            request.PayloadRef, ApprovalStatus.Pending, now, now + ttl, null, null, null);
        await store.InsertAsync(item, ct).ConfigureAwait(false);
        await AuditAsync("approval.requested", request.RequesterPrincipalId, item, "pending", now, ct).ConfigureAwait(false);
        return item.Id;
    }

    public async Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default) =>
        Evaluate(await store.GetAsync(id, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, CancellationToken ct = default) =>
        await store.ListPendingAsync(kind, clock.GetUtcNow(), ct).ConfigureAwait(false);

    public async Task<ApprovalItem> DecideAsync(Guid id, Guid approverPrincipalId, bool approve, string? reason, CancellationToken ct = default)
    {
        var item = Evaluate(await store.GetAsync(id, ct).ConfigureAwait(false))
            ?? throw new ApprovalRefusedException(ApprovalRefusal.NotFound, "No such approval request.");
        if (item.Status != ApprovalStatus.Pending)
            throw new ApprovalRefusedException(ApprovalRefusal.NotPending, $"The request is {item.Status.ToString().ToLowerInvariant()}.");
        if (item.RequesterPrincipalId == approverPrincipalId)
            throw new ApprovalRefusedException(ApprovalRefusal.SelfApproval, "A requester may not decide their own request.");

        var kind = await KindOfAsync(approverPrincipalId, ct).ConfigureAwait(false);
        if (kind is null || !await permissions.IsAllowedAsync(approverPrincipalId, kind.Value, Capabilities.Approve, Scope.Instance, ct).ConfigureAwait(false))
            throw new ApprovalRefusedException(ApprovalRefusal.NotPermitted, "The approver lacks the approval capability.");
        if (item.Tier == 2 && kind != PrincipalKind.Human)
            throw new ApprovalRefusedException(ApprovalRefusal.HumanRequired, "A tier 2 request needs a human approver.");

        var now = clock.GetUtcNow();
        if (!await store.TryDecideAsync(id, approverPrincipalId, approve, reason, now, ct).ConfigureAwait(false))
            throw new ApprovalRefusedException(ApprovalRefusal.NotPending, "The request is no longer pending.");

        var decided = item with
        {
            Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Denied,
            DecidedBy = approverPrincipalId,
            DecidedAt = now,
            Reason = reason,
        };
        await AuditAsync("approval.decided", approverPrincipalId, decided, approve ? "approved" : "denied", now, ct).ConfigureAwait(false);
        return decided;
    }

    public async Task<bool> TryConsumeAsync(Guid id, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (!await store.TryConsumeAsync(id, now, ct).ConfigureAwait(false))
            return false;
        var item = await store.GetAsync(id, ct).ConfigureAwait(false);
        await AuditAsync("approval.consumed", item?.DecidedBy ?? Guid.Empty, item, "consumed", now, ct, id).ConfigureAwait(false);
        return true;
    }

    // A pending item past its expiry reads as expired; nothing is written.
    private ApprovalItem? Evaluate(ApprovalItem? item) =>
        item is { Status: ApprovalStatus.Pending } && item.ExpiresAt <= clock.GetUtcNow() ? item with { Status = ApprovalStatus.Expired } : item;

    // The principal's kind comes from its role assignments; an agent assignment wins, so a mixed principal is treated as an agent.
    private async Task<PrincipalKind?> KindOfAsync(Guid principalId, CancellationToken ct)
    {
        var assignments = await roles.AssignmentsForPrincipalAsync(principalId, ct).ConfigureAwait(false);
        if (assignments.Count == 0)
            return null;
        return assignments.Any(a => a.PrincipalKind == PrincipalKind.Agent) ? PrincipalKind.Agent : assignments[0].PrincipalKind;
    }

    // Ids, tier, kind and outcome only: never the subject, the payload reference or the reason.
    private Task AuditAsync(string auditKind, Guid actor, ApprovalItem? item, string outcome, DateTimeOffset now, CancellationToken ct, Guid? fallbackId = null)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", item?.Id ?? fallbackId ?? Guid.Empty);
            if (item is not null)
            {
                w.WriteString("kind", item.Kind);
                w.WriteNumber("tier", item.Tier);
            }

            w.WriteString("outcome", outcome);
            w.WriteEndObject();
        }

        return audit.AppendAsync(new AuditEntry(now, actor.ToString("D"), auditKind, Encoding.UTF8.GetString(buffer.ToArray())), ct);
    }
}
