using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Kanban;

/// <summary>Payload of the <c>board.move</c> request: one card, the column to move it to, and the revision the client last read (doc 12).</summary>
/// <param name="ProjectId">Project the card belongs to.</param>
/// <param name="CardId">Task id of the card.</param>
/// <param name="ToState">Target column: "backlog", "ready", "in_progress", "review" or "done".</param>
/// <param name="ExpectedRevision">The card's revision as the client last read it. The move applies only if it still matches. Must be 1 or more.</param>
/// <param name="Reason">Why the card moves. Needed only for the moves the transition table says need one.</param>
[WirePayload(WireTypes.BoardMove)]
public sealed record KanbanMoveRequest(Guid ProjectId, Guid CardId, string ToState, long ExpectedRevision, string? Reason = null);

/// <summary>The wire spelling of <see cref="KanbanMoveReply.Status"/>.</summary>
public static class KanbanMoveStatuses
{
    /// <summary>The move was applied; <see cref="KanbanMoveReply.Revision"/> is the new revision.</summary>
    public const string Applied = "applied";

    /// <summary>The expected revision was stale; <see cref="KanbanMoveReply.Revision"/> is the current one. Nothing changed.</summary>
    public const string Conflict = "conflict";

    /// <summary>A guard or rule refused the move. Nothing changed.</summary>
    public const string Refused = "refused";

    /// <summary>The request was not valid (unknown card, bad state, move not in the table). Nothing changed.</summary>
    public const string Invalid = "invalid";
}

/// <summary>Reply to <c>board.move</c>. Every outcome of a move is a reply; only a malformed payload or a denied principal is a wire error.</summary>
/// <param name="Status">One of <see cref="KanbanMoveStatuses"/>.</param>
/// <param name="Revision">New revision when applied, current revision on a conflict, otherwise null.</param>
/// <param name="BlockerTitles">Titles of the unfinished prerequisites when a refusal is caused by them, otherwise empty.</param>
/// <param name="Reason">Why the move was refused or invalid, in words the owner can read. Null when applied or in conflict.</param>
[WirePayload]
public sealed record KanbanMoveReply(string Status, long? Revision, IReadOnlyList<string> BlockerTitles, string? Reason = null);
