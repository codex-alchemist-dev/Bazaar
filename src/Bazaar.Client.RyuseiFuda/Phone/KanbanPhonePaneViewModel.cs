using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.RyuseiFuda.Workday;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// The Kanban board on the phone (doc 29, work package 9; doc 12). It loads the board through <see cref="IKanbanBoardClient"/>
/// and moves a card only with the card's revision as last read. A stale revision is a conflict: the move is not applied,
/// the board is reloaded, and <see cref="Status"/> says why. A refused move leaves the board as it was and shows the reason.
/// Avalonia-free; the view is <see cref="KanbanPhonePaneView"/>.
/// </summary>
public sealed class KanbanPhonePaneViewModel : IPaneViewModel
{
    /// <summary>The pane heading. Matches the heading the phone pager already shows for the board.</summary>
    public const string PaneTitle = "RyuseiFuda · Board";

    private const string _loadFailedText = "Could not load the board.";

    private readonly IKanbanBoardClient _client;
    private readonly Guid _projectId;
    private IReadOnlyList<KanbanPhoneCard> _cards = [];
    private IReadOnlyList<KanbanPhoneColumn> _columns = [];
    private bool _busy;

    /// <param name="client">The board service the head supplies.</param>
    /// <param name="projectId">The project whose board the pane shows.</param>
    public KanbanPhonePaneViewModel(IKanbanBoardClient client, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _projectId = projectId;
        Rebuild();
    }

    public string Kind => PaneKinds.Kanban;

    public string Title => PaneTitle;

    /// <summary>The five columns in board order, always all five, even when empty.</summary>
    public IReadOnlyList<KanbanPhoneColumn> Columns => _columns;

    /// <summary>The last outcome shown to the user (a conflict, a refusal, a failed load), or null when none is shown.</summary>
    public string? Status { get; private set; }

    /// <summary>True while a move is in flight. A second move is ignored until it completes.</summary>
    public bool IsBusy => _busy;

    /// <summary>Raised whenever the board, the status or the busy flag changes, so a view can redraw.</summary>
    public event EventHandler? Changed;

    /// <summary>Loads the board. A failed load keeps the board on screen and shows a status line.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Status = null;
        if (!await RefreshAsync(ct).ConfigureAwait(true))
        {
            Status = _loadFailedText;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves a card to <paramref name="toState"/> with the revision the pane holds for it. Returns the result, or null when
    /// nothing was sent: another move is in flight, the card is not on the board, or it is already in that column.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="toState"/> is not one of the five stored states.</exception>
    public async Task<KanbanMoveResult?> MoveAsync(Guid cardId, string toState, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toState);
        var targetTitle = KanbanStates.TitleOf(toState);
        if (_busy)
        {
            return null;
        }

        var card = _cards.FirstOrDefault(candidate => candidate.Id == cardId);
        if (card is null)
        {
            Status = "That card is no longer on the board.";
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }

        if (card.State == toState)
        {
            return null;
        }

        _busy = true;
        Status = null;
        Changed?.Invoke(this, EventArgs.Empty);
        KanbanMoveResult result;
        try
        {
            // The only revision sent is the card's own. The board's state is never written back from the pane.
            result = await _client.MoveAsync(_projectId, card.Id, toState, card.Revision, ct).ConfigureAwait(true);
            switch (result)
            {
                case KanbanMoveResult.Applied:
                    // Reload so the dependents' Blocked marks follow the move.
                    Status = await RefreshAsync(ct).ConfigureAwait(true)
                        ? $"Moved to {targetTitle}."
                        : _loadFailedText;
                    break;

                case KanbanMoveResult.Conflict:
                    // Not overwritten: the server's card changed first. Reload and let the user decide again.
                    Status = await RefreshAsync(ct).ConfigureAwait(true)
                        ? "This card changed on the server, so your move was not applied. The board was reloaded."
                        : "This card changed on the server, so your move was not applied. The board could not be reloaded.";
                    break;

                case KanbanMoveResult.Refused refused:
                    Status = refused.BlockerTitles.Count == 0
                        ? refused.Reason
                        : $"{refused.Reason} Blocked by {string.Join(", ", refused.BlockerTitles)}.";
                    break;

                case KanbanMoveResult.Invalid invalid:
                    Status = invalid.Reason;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown Kanban move result '{result.GetType().Name}'.");
            }
        }
        finally
        {
            _busy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>
    /// Reads the board. Returns false when the service failed or sent a card in a state the phone does not know; the last
    /// board stays on screen then. A failure of any kind is a status line, not a crash.
    /// </summary>
    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        try
        {
            var board = await _client.LoadAsync(_projectId, ct).ConfigureAwait(true);
            foreach (var card in board.Cards)
            {
                // Throws for a state outside the five, so an unknown card is never dropped from the board silently.
                _ = KanbanStates.TitleOf(card.State);
            }

            _cards = board.Cards;
            Rebuild();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private void Rebuild()
    {
        _columns = KanbanStates.All
            .Select(state => new KanbanPhoneColumn(
                state,
                KanbanStates.TitleOf(state),
                _cards
                    .Where(card => card.State == state)
                    .OrderBy(card => card.Title, StringComparer.Ordinal)
                    .ThenBy(card => card.Id)
                    .ToList()))
            .ToList();
    }
}
