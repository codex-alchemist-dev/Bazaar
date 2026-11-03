using Avalonia;
using Avalonia.Controls;

namespace Bazaar.Client.Core.Kanban;

/// <summary>
/// Renders a <see cref="KanbanBoardViewModel"/>: five column headers, the cards, and the last refusal.
/// The refusal line is refreshed from the view model's <see cref="KanbanBoardViewModel.Changed"/> event.
/// </summary>
public sealed partial class KanbanBoardView : UserControl
{
    private KanbanBoardViewModel? _board;

    public KanbanBoardView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_board is not null)
        {
            _board.Changed -= OnBoardChanged;
        }

        _board = DataContext as KanbanBoardViewModel;

        if (_board is not null)
        {
            _board.Changed += OnBoardChanged;
        }

        ShowRefusal();
    }

    private void OnBoardChanged(object? sender, EventArgs e) => ShowRefusal();

    private void ShowRefusal()
    {
        var message = _board?.RefusalMessage;
        RefusalText.Text = message;
        RefusalText.IsVisible = message is not null;
    }
}
