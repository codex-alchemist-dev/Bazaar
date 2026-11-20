using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bazaar.Client.RyuseiFuda.Phone;
using Bazaar.Client.RyuseiFuda.Workday;
using static Bazaar.Client.Core.Tests.Kanban.Phone.PhoneCards;

namespace Bazaar.Client.Core.Tests.Kanban.Phone;

public class KanbanPhonePaneViewTests
{
    [AvaloniaFact]
    public async Task Phone_body_shows_the_five_columns_with_their_counts()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"), Card(2, "Auth", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var window = Show(pane);

        var headers = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Classes.Contains("column-title"))
            .Select(text => text.Text)
            .ToList();

        Assert.Equal(["Backlog (0)", "Ready (2)", "In progress (0)", "Review (0)", "Done (0)"], headers);
    }

    [AvaloniaFact]
    public async Task A_move_button_applies_the_move_and_redraws_the_columns()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 4));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var window = Show(pane);

        Press(window, "Move Schema to In progress");

        Assert.Equal([new MoveCall(Id(1), KanbanStates.InProgress, 4)], client.Moves);
        var texts = Texts(window);
        Assert.Contains("In progress (1)", texts);
        Assert.Contains("Ready (0)", texts);
        Assert.Contains("Moved to In progress.", texts);
    }

    [AvaloniaFact]
    public async Task A_stale_move_shows_the_conflict_and_the_reloaded_card()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 4));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var window = Show(pane);
        client.ChangeOnServer(Card(1, "Schema", "review", revision: 5));

        Press(window, "Move Schema to Done");

        Assert.Single(client.Moves);
        var texts = Texts(window);
        Assert.Contains(texts, text => text.Contains("changed on the server", StringComparison.Ordinal));
        Assert.Contains("Review (1)", texts);
        Assert.Contains("Ready (0)", texts);
    }

    [AvaloniaFact]
    public async Task A_refusal_names_the_blockers_in_the_body()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Dependent", "ready", 1, "Schema"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var window = Show(pane);

        Press(window, "Move Dependent to In progress");

        var status = window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "BoardStatus");
        Assert.Equal("Finish the prerequisites first. Blocked by Schema.", status.Text);
        Assert.Contains("Ready (1)", Texts(window));
    }

    [AvaloniaFact]
    public async Task Move_buttons_are_disabled_while_a_move_is_in_flight()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 1))
        {
            HeldMove = new TaskCompletionSource<KanbanMoveResult>(),
        };
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var window = Show(pane);

        Press(window, "Move Schema to In progress");

        Assert.True(pane.IsBusy);
        Assert.All(Buttons(window), button => Assert.False(button.IsEnabled));

        client.HeldMove.SetResult(new KanbanMoveResult.Applied(2));
        Dispatcher.UIThread.RunJobs();

        Assert.False(pane.IsBusy);
        Assert.All(Buttons(window), button => Assert.True(button.IsEnabled));
    }

    [AvaloniaFact]
    public async Task Each_card_offers_one_move_button_per_other_column()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var window = Show(pane);

        var names = Buttons(window).Select(button => AutomationProperties.GetName(button)).ToList();

        Assert.Equal(
            ["Move Schema to Backlog", "Move Schema to In progress", "Move Schema to Review", "Move Schema to Done"],
            names);
    }

    private static Window Show(KanbanPhonePaneViewModel pane)
    {
        var window = new Window { Content = KanbanPhonePaneView.Create(pane), Width = 360, Height = 740 };
        window.Show();
        return window;
    }

    private static void Press(Window window, string automationName)
    {
        var button = Buttons(window).Single(candidate => AutomationProperties.GetName(candidate) == automationName);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static List<Button> Buttons(Window window) => window.GetLogicalDescendants().OfType<Button>().ToList();

    private static List<string> Texts(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToList();
}
