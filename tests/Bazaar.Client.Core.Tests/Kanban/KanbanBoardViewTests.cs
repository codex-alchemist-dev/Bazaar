using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Kanban;

namespace Bazaar.Client.Core.Tests.Kanban;

public class KanbanBoardViewTests
{
    [AvaloniaFact]
    public void Board_view_renders_five_column_headers_and_the_refusal_message()
    {
        var cardId = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
        var board = new KanbanBoardViewModel(
        [
            new KanbanCard(cardId, "Dependent", "ready", null, [Guid.NewGuid()], ["Base"]),
        ]);
        var view = new KanbanBoardView { DataContext = board };
        var window = new Window { Content = view };
        window.Show();
        using var _ = window.CaptureRenderedFrame(); // forces layout so the item templates materialise

        var headers = view.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Classes.Contains("column-title"))
            .Select(text => text.Text)
            .ToList();
        Assert.Equal(["Backlog", "Ready", "In progress", "Review", "Done"], headers);

        var refusal = view.FindControl<TextBlock>("RefusalText");
        Assert.NotNull(refusal);
        Assert.False(refusal.IsVisible);

        board.Refuse(cardId, "Blocked by Base: finish it first.");

        Assert.True(refusal.IsVisible);
        Assert.Equal("Blocked by Base: finish it first.", refusal.Text);
    }
}
