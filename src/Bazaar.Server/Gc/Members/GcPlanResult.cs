using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Members;

/// <summary>Why a planner refused. Every refusal is a planner decision, not a database error.</summary>
public enum GcPlanRefusal
{
    /// <summary>The GC is archived and therefore read-only.</summary>
    Archived,

    /// <summary>The actor lacks the role or relationship the action needs.</summary>
    NotPermitted,

    /// <summary>Invites are owner-only under the current policy.</summary>
    OwnerOnlyInvites,

    /// <summary>The principal already has an active row in this GC.</summary>
    AlreadyMember,

    /// <summary>The target has no active row to change.</summary>
    NoActiveMembership,

    /// <summary>The owner has no membership row; it cannot be added.</summary>
    OwnerHasNoRow,

    /// <summary>The DM already holds its two members.</summary>
    DmFull,

    /// <summary>Thinking GC membership is fixed to its agent.</summary>
    ThinkingMembershipFixed,

    /// <summary>Unarchive was asked for a GC that is not archived.</summary>
    NotArchived,

    /// <summary>Archive was asked for a GC that is already archived.</summary>
    AlreadyArchived,

    /// <summary>Only DM and Thinking GCs take access requests.</summary>
    NotRequestable,

    /// <summary>The requester can already read up to the current head.</summary>
    AlreadyReadable,

    /// <summary>An access request for this principal is already pending.</summary>
    DuplicatePendingRequest,

    /// <summary>There is no pending request to decide.</summary>
    NoPendingRequest,

    /// <summary>The approver is not allowed to decide under the current policy.</summary>
    NotApprover,

    /// <summary>A Thinking GC cannot be nested under another Thinking GC.</summary>
    WorkingGcIsThinking,

    /// <summary>The agent is not an active member with the member role in the working GC.</summary>
    AgentNotWorkingMember,

    /// <summary>The agent already has a Thinking GC under this working GC.</summary>
    AgentAlreadyHasThinkingGc,
}

/// <summary>
/// A planner's answer. Planners return drafts only: the writer assigns seq and event ids, then persists
/// <see cref="Gc"/> (new header state), <see cref="Member"/> (new membership state) and <see cref="Event"/>.
/// </summary>
/// <param name="Refusal">Set when the action is refused; the other fields are then null.</param>
/// <param name="Gc">New header state, for archive and unarchive and for a created Thinking GC.</param>
/// <param name="Member">New membership state row to insert.</param>
/// <param name="Event">System event to append, or null when the plan has none.</param>
public sealed record GcPlanResult(
    GcPlanRefusal? Refusal,
    GcRecord? Gc,
    GcMemberRecord? Member,
    GcEventDraft? Event)
{
    /// <summary>True when the plan carries changes and no refusal.</summary>
    public bool Succeeded => Refusal is null;

    /// <summary>A successful plan.</summary>
    public static GcPlanResult Ok(GcRecord? gc, GcMemberRecord? member, GcEventDraft? ev) => new(null, gc, member, ev);

    /// <summary>A refused plan.</summary>
    public static GcPlanResult Refused(GcPlanRefusal refusal) => new(refusal, null, null, null);
}
