using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Members;

/// <summary>
/// Pure membership, archive and access-request planners (docs/design/01-gc-data-model.md). Each method checks the
/// actor and returns a draft. Nothing is written here: the writer (Gc/Write) assigns the real seq, persists the
/// returned rows and appends the system event.
/// </summary>
/// <remarks>
/// Seq convention: <c>headSeq</c> is the GC's current head. A plan that adds or removes a row uses
/// <c>headSeq + 1</c> for the row's JoinedSeq or LeftSeq, which is the seq of its own system event. The writer
/// must append that event at exactly headSeq + 1, or it must overwrite these values.
/// </remarks>
public static class GcMembershipPlanner
{
    /// <summary>Adds a principal with the given role. Any member may invite unless the policy is owner-only.</summary>
    /// <param name="headSeq">Current head seq of the GC.</param>
    public static GcPlanResult PlanAddMember(
        GcRecord gc,
        IReadOnlyCollection<GcMemberRecord> members,
        GcViewer actor,
        Guid newPrincipalId,
        PrincipalKind newKind,
        GcMemberRole role,
        long headSeq,
        GcInvitePolicy invitePolicy)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(actor);

        if (gc.ArchivedAt is not null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.Archived);
        }

        if (GcTypeKeys.IsThinking(gc))
        {
            return GcPlanResult.Refused(GcPlanRefusal.ThinkingMembershipFixed);
        }

        if (newKind == PrincipalKind.Owner)
        {
            return GcPlanResult.Refused(GcPlanRefusal.OwnerHasNoRow);
        }

        if (!GcAccessPolicy.CanWrite(gc, members, actor))
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotPermitted);
        }

        if (invitePolicy == GcInvitePolicy.OwnerOnly && actor.Kind != PrincipalKind.Owner)
        {
            return GcPlanResult.Refused(GcPlanRefusal.OwnerOnlyInvites);
        }

        if (GcAccessPolicy.IsActive(GcAccessPolicy.CurrentRow(gc, members, newPrincipalId)))
        {
            return GcPlanResult.Refused(GcPlanRefusal.AlreadyMember);
        }

        if (GcTypeKeys.IsDm(gc) && GcAccessPolicy.ActiveCount(gc, members) >= GcTypeKeys.DmMemberLimit)
        {
            return GcPlanResult.Refused(GcPlanRefusal.DmFull);
        }

        var row = new GcMemberRecord(gc.GcId, newPrincipalId, newKind, role, headSeq + 1, null);
        return GcPlanResult.Ok(null, row, SystemEvent(gc, actor, Payload("member_added", actor, newPrincipalId, role)));
    }

    /// <summary>
    /// Removes a principal. The owner may remove anyone; a member may remove only themselves. Returns the new row
    /// state with LeftSeq = headSeq + 1. The row itself is not updated: the writer inserts this state.
    /// </summary>
    public static GcPlanResult PlanRemoveMember(
        GcRecord gc,
        IReadOnlyCollection<GcMemberRecord> members,
        GcViewer actor,
        Guid targetPrincipalId,
        long headSeq)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(actor);

        if (gc.ArchivedAt is not null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.Archived);
        }

        if (GcTypeKeys.IsThinking(gc))
        {
            return GcPlanResult.Refused(GcPlanRefusal.ThinkingMembershipFixed);
        }

        var row = GcAccessPolicy.CurrentRow(gc, members, targetPrincipalId);
        if (!GcAccessPolicy.IsActive(row))
        {
            return GcPlanResult.Refused(GcPlanRefusal.NoActiveMembership);
        }

        if (actor.Kind != PrincipalKind.Owner && actor.PrincipalId != targetPrincipalId)
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotPermitted);
        }

        var left = row with { LeftSeq = headSeq + 1 };
        return GcPlanResult.Ok(null, left, SystemEvent(gc, actor, Payload("member_removed", actor, targetPrincipalId, row.Role)));
    }

    /// <summary>Archives a GC. Owner only. Children are not archived.</summary>
    /// <param name="now">Archive time, supplied by the caller so the plan stays deterministic.</param>
    public static GcPlanResult PlanArchive(GcRecord gc, GcViewer actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.Kind != PrincipalKind.Owner)
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotPermitted);
        }

        if (gc.ArchivedAt is not null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.AlreadyArchived);
        }

        var archived = gc with { ArchivedAt = now };
        return GcPlanResult.Ok(archived, null, SystemEvent(gc, actor, Payload("archived", actor, null, null)));
    }

    /// <summary>
    /// Unarchives a GC. The owner always may. Any active member with the member role may when the policy is
    /// <see cref="GcUnarchivePolicy.AnyMember"/>.
    /// </summary>
    public static GcPlanResult PlanUnarchive(
        GcRecord gc,
        IReadOnlyCollection<GcMemberRecord> members,
        GcViewer actor,
        GcUnarchivePolicy unarchivePolicy)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(actor);

        if (gc.ArchivedAt is null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotArchived);
        }

        var allowed = actor.Kind == PrincipalKind.Owner
            || (unarchivePolicy == GcUnarchivePolicy.AnyMember
                && GcAccessPolicy.IsActiveMemberWithRole(gc, members, actor.PrincipalId, GcMemberRole.Member));
        if (!allowed)
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotPermitted);
        }

        var restored = gc with { ArchivedAt = null };
        return GcPlanResult.Ok(restored, null, SystemEvent(gc, actor, Payload("unarchived", actor, null, null)));
    }

    /// <summary>
    /// Files an access request on a DM or Thinking GC. The requester must not already read up to the head.
    /// A former member whose LeftSeq is below the head may request again. Emits <c>access_requested</c>.
    /// </summary>
    /// <param name="approvedAccessPrincipals">Principals with an approved request for this GC.</param>
    /// <param name="headSeq">Current head seq of the GC.</param>
    /// <param name="requestPending">True when the writer finds a pending request from this requester.</param>
    public static GcPlanResult PlanAccessRequest(
        GcRecord gc,
        IReadOnlyCollection<GcMemberRecord> members,
        IReadOnlySet<Guid> approvedAccessPrincipals,
        GcViewer requester,
        long headSeq,
        bool requestPending)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(approvedAccessPrincipals);
        ArgumentNullException.ThrowIfNull(requester);

        if (gc.ArchivedAt is not null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.Archived);
        }

        if (!GcTypeKeys.IsDm(gc) && !GcTypeKeys.IsThinking(gc))
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotRequestable);
        }

        var readable = GcAccessPolicy.ReadableUpToSeq(gc, members, requester, approvedAccessPrincipals);
        if (readable is { } upTo && upTo >= headSeq)
        {
            return GcPlanResult.Refused(GcPlanRefusal.AlreadyReadable);
        }

        if (requestPending)
        {
            return GcPlanResult.Refused(GcPlanRefusal.DuplicatePendingRequest);
        }

        return GcPlanResult.Ok(null, null, SystemEvent(gc, requester, Payload("access_requested", requester, requester.PrincipalId, null)));
    }

    /// <summary>
    /// Approves or denies a pending access request. The owner always may. A DM member may only when the policy is
    /// <see cref="GcDmAccessApprovers.OwnerOrDmMember"/> and the GC is a DM. Thinking GCs are owner-decided.
    /// Emits <c>access_approved</c> or <c>access_denied</c>. The writer stores the decision.
    /// </summary>
    public static GcPlanResult PlanAccessDecision(
        GcRecord gc,
        IReadOnlyCollection<GcMemberRecord> members,
        GcViewer approver,
        Guid requesterId,
        bool approve,
        GcDmAccessApprovers dmApprovers,
        bool requestPending)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(approver);

        if (gc.ArchivedAt is not null)
        {
            return GcPlanResult.Refused(GcPlanRefusal.Archived);
        }

        if (!CanApprove(gc, members, approver, dmApprovers))
        {
            return GcPlanResult.Refused(GcPlanRefusal.NotApprover);
        }

        if (!requestPending)
        {
            return GcPlanResult.Refused(GcPlanRefusal.NoPendingRequest);
        }

        var action = approve ? "access_approved" : "access_denied";
        return GcPlanResult.Ok(null, null, SystemEvent(gc, approver, Payload(action, approver, requesterId, null)));
    }

    private static bool CanApprove(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members, GcViewer approver, GcDmAccessApprovers dmApprovers)
    {
        if (approver.Kind == PrincipalKind.Owner)
        {
            return true;
        }

        return dmApprovers == GcDmAccessApprovers.OwnerOrDmMember
            && GcTypeKeys.IsDm(gc)
            && GcAccessPolicy.IsActive(GcAccessPolicy.CurrentRow(gc, members, approver.PrincipalId));
    }

    private static GcEventDraft SystemEvent(GcRecord gc, GcViewer actor, string payloadJson)
    {
        var (authorKind, trust) = actor.Kind switch
        {
            PrincipalKind.Owner => (AuthorKind.Owner, SourceTrust.Owner),
            PrincipalKind.Agent => (AuthorKind.Agent, SourceTrust.Agent),
            _ => (AuthorKind.Human, SourceTrust.MemberHuman),
        };

        return new GcEventDraft(gc.GcId, GcEventKinds.System, authorKind, actor.PrincipalId, trust, PayloadJson: payloadJson);
    }

    private static string Payload(string action, GcViewer actor, Guid? principalId, GcMemberRole? role)
    {
        var node = new JsonObject
        {
            ["action"] = action,
            ["actor"] = actor.PrincipalId.ToString("D"),
        };

        if (principalId is { } principal)
        {
            node["principal"] = principal.ToString("D");
        }

        if (role is { } memberRole)
        {
            node["role"] = memberRole == GcMemberRole.Observer ? "observer" : "member";
        }

        return node.ToJsonString();
    }
}
