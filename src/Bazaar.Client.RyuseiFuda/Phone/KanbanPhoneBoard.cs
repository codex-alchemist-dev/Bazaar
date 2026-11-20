namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// One card as the phone holds it. <see cref="Revision"/> is the card's revision (the task row version, doc 12) and is the
/// only value a move is checked against. Blocked is not stored: a card is blocked while <see cref="UnfinishedBlockerIds"/>
/// is not empty.
/// </summary>
/// <param name="Id">Task id.</param>
/// <param name="Title">Task title.</param>
/// <param name="State">Stored column: "backlog", "ready", "in_progress", "review" or "done".</param>
/// <param name="Revision">Revision the card had when it was read.</param>
/// <param name="UnfinishedBlockerIds">Prerequisites that are not Done, in edge order.</param>
/// <param name="BlockerTitles">Titles of the unfinished prerequisites, in the same order.</param>
public sealed record KanbanPhoneCard(
    Guid Id,
    string Title,
    string State,
    long Revision,
    IReadOnlyList<Guid> UnfinishedBlockerIds,
    IReadOnlyList<string> BlockerTitles)
{
    /// <summary>True while at least one prerequisite is unfinished.</summary>
    public bool Blocked => UnfinishedBlockerIds.Count > 0;
}

/// <summary>One board column: a stored state, its title, and its cards in display order.</summary>
public sealed record KanbanPhoneColumn(string State, string Title, IReadOnlyList<KanbanPhoneCard> Cards);

/// <summary>A whole project board as the phone loaded it.</summary>
/// <param name="ProjectId">Project the board belongs to.</param>
/// <param name="Cards">Cards in the order the service returned them.</param>
public sealed record KanbanPhoneBoard(Guid ProjectId, IReadOnlyList<KanbanPhoneCard> Cards);
