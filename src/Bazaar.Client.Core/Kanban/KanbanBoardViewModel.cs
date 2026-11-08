namespace Bazaar.Client.Core.Kanban;

/// <summary>
/// The five stored Kanban states. Blocked is a derived badge, never a state.
/// </summary>
public static class KanbanStates
{
    public const string Backlog = "backlog";
    public const string Ready = "ready";
    public const string InProgress = "in_progress";
    public const string Review = "review";
    public const string Done = "done";

    /// <summary>Column title for a stored state.</summary>
    /// <exception cref="ArgumentException">The state is not one of the five.</exception>
    public static string TitleOf(string state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state switch
        {
            Backlog => "Backlog",
            Ready => "Ready",
            InProgress => "In progress",
            Review => "Review",
            Done => "Done",
            _ => throw new ArgumentException($"Unknown Kanban state '{state}'.", nameof(state)),
        };
    }
}

/// <summary>
/// A card as the board renders it. Blocked is derived from the unfinished blockers, whatever the column.
/// </summary>
/// <param name="Card">The underlying card.</param>
public sealed record KanbanCardViewModel(KanbanCard Card)
{
    public Guid Id => Card.Id;

    public string Title => Card.Title;

    public string State => Card.State;

    public Guid? OwnerAgentId => Card.OwnerAgentId;

    /// <summary>True when at least one prerequisite is unfinished.</summary>
    public bool Blocked => Card.UnfinishedBlockerIds.Count > 0;

    /// <summary>Blocker titles for display.</summary>
    public IReadOnlyList<string> BlockerChips => Card.BlockerTitles;
}

/// <summary>
/// One board column: a title and its cards in display order.
/// </summary>
/// <param name="State">Stored state the column holds.</param>
/// <param name="Title">Column header.</param>
/// <param name="Cards">Cards ordered by title, then id.</param>
public sealed record KanbanColumnViewModel(string State, string Title, IReadOnlyList<KanbanCardViewModel> Cards);

/// <summary>
/// A refused move, shown on the board until cleared.
/// </summary>
/// <param name="CardId">The card whose move was refused.</param>
/// <param name="Reason">Why the server or the client refused it.</param>
public sealed record KanbanRefusal(Guid CardId, string Reason);

/// <summary>
/// Board state for the Kanban view: five columns in fixed order, plus the last refused move.
/// Avalonia-free. Raises <see cref="Changed"/> when the refusal changes, so a view can refresh.
/// </summary>
public sealed class KanbanBoardViewModel
{
    private static readonly string[] _states =
    [
        KanbanStates.Backlog,
        KanbanStates.Ready,
        KanbanStates.InProgress,
        KanbanStates.Review,
        KanbanStates.Done,
    ];

    private readonly HashSet<Guid> _cardIds;
    private KanbanRefusal? _lastRefusal;

    /// <summary>Builds the board from a snapshot of cards.</summary>
    /// <exception cref="ArgumentException">A card has a state that is not one of the five.</exception>
    public KanbanBoardViewModel(IEnumerable<KanbanCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var viewModels = new List<KanbanCardViewModel>();
        foreach (var card in cards)
        {
            // Rejects a state outside the five here, so no card is dropped silently.
            _ = KanbanStates.TitleOf(card.State);
            viewModels.Add(new KanbanCardViewModel(card));
        }

        _cardIds = viewModels.Select(card => card.Id).ToHashSet();

        Columns = _states
            .Select(state => new KanbanColumnViewModel(
                state,
                KanbanStates.TitleOf(state),
                viewModels
                    .Where(card => card.State == state)
                    .OrderBy(card => card.Title, StringComparer.Ordinal)
                    .ThenBy(card => card.Id)
                    .ToList()))
            .ToList();
    }

    /// <summary>The five columns: Backlog, Ready, In progress, Review, Done.</summary>
    public IReadOnlyList<KanbanColumnViewModel> Columns { get; }

    /// <summary>The last refused move, or null when none is shown.</summary>
    public KanbanRefusal? LastRefusal => _lastRefusal;

    /// <summary>Text of the last refusal, or null when none is shown.</summary>
    public string? RefusalMessage => _lastRefusal?.Reason;

    /// <summary>Raised after the refusal changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Stores the reason a move was refused, replacing any earlier refusal.</summary>
    /// <exception cref="ArgumentException">The card is not on this board, or the reason is empty.</exception>
    public void Refuse(Guid cardId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!_cardIds.Contains(cardId))
        {
            throw new ArgumentException($"Card {cardId} is not on this board.", nameof(cardId));
        }

        _lastRefusal = new KanbanRefusal(cardId, reason);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the shown refusal, if any.</summary>
    public void ClearRefusal()
    {
        if (_lastRefusal is null)
        {
            return;
        }

        _lastRefusal = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
