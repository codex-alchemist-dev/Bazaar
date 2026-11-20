namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// The outcome of a revision-checked card move (doc 12, <c>TransitionResult</c>). Each outcome is a result the pane shows;
/// none of them is an exception.
/// </summary>
public abstract record KanbanMoveResult
{
    /// <summary>The move was applied. <paramref name="NewRevision"/> is the card's revision after the move.</summary>
    public sealed record Applied(long NewRevision) : KanbanMoveResult;

    /// <summary>
    /// The card's revision moved on since the phone read it, so nothing was changed. <paramref name="CurrentRevision"/> is
    /// what the server holds now. The pane reloads the board instead of retrying the move.
    /// </summary>
    public sealed record Conflict(long CurrentRevision) : KanbanMoveResult;

    /// <summary>
    /// The server refused the move, for example because a prerequisite is unfinished. Nothing changed.
    /// </summary>
    /// <param name="BlockerTitles">Titles of the unfinished prerequisites, when the refusal names them.</param>
    /// <param name="Reason">Why the move was refused, in words the user can read.</param>
    public sealed record Refused(IReadOnlyList<string> BlockerTitles, string Reason) : KanbanMoveResult;

    /// <summary>The request itself was not valid (for example an unknown target state). Nothing changed.</summary>
    /// <param name="Reason">Why the request was invalid.</param>
    public sealed record Invalid(string Reason) : KanbanMoveResult;
}
