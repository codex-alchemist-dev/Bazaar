using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Kanban;

/// <summary>Payload of the <c>board.assign</c> request: one card, its new assignee, and the revision the client last read (doc 12, Assign authority and command).</summary>
/// <param name="ProjectId">Project the card belongs to.</param>
/// <param name="CardId">Task id of the card.</param>
/// <param name="AssigneeAgentId">Agent that becomes the assignee, or null to clear the assignee.</param>
/// <param name="ExpectedRevision">The card's revision as the client last read it. The assignment applies only if it still matches. Must be 1 or more.</param>
[WirePayload(WireTypes.BoardAssign)]
public sealed record KanbanAssignRequest(Guid ProjectId, Guid CardId, Guid? AssigneeAgentId, long ExpectedRevision);

/// <summary>The wire spelling of <see cref="KanbanAssignReply.Status"/>. Same words as <see cref="KanbanMoveStatuses"/>.</summary>
public static class KanbanAssignStatuses
{
    /// <summary>The assignee was set; <see cref="KanbanAssignReply.Revision"/> is the new revision.</summary>
    public const string Applied = "applied";

    /// <summary>The expected revision was stale; <see cref="KanbanAssignReply.Revision"/> is the current one. Nothing changed.</summary>
    public const string Conflict = "conflict";

    /// <summary>The assignee is already that agent, or the actor may not assign. Nothing changed.</summary>
    public const string Refused = "refused";

    /// <summary>The request was not valid (unknown card, empty ids, bad revision). Nothing changed.</summary>
    public const string Invalid = "invalid";
}

/// <summary>Reply to <c>board.assign</c>. Every outcome is a reply; only a malformed payload or a denied principal is a wire error.</summary>
/// <param name="Status">One of <see cref="KanbanAssignStatuses"/>.</param>
/// <param name="Revision">New revision when applied, current revision on a conflict, otherwise null.</param>
/// <param name="Reason">Why the request was refused or invalid, in words the owner can read. Null when applied or in conflict.</param>
[WirePayload]
public sealed record KanbanAssignReply(string Status, long? Revision, string? Reason = null);
