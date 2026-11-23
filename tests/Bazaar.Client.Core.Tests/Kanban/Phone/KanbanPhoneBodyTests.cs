using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bazaar.Client.RyuseiFuda.Phone;
using static Bazaar.Client.Core.Tests.Kanban.Phone.PhoneCards;

namespace Bazaar.Client.Core.Tests.Kanban.Phone;

public class KanbanPhoneBodyTests
{
    [AvaloniaFact]
    public async Task The_body_loads_the_board_of_the_chosen_project()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var window = new Window { Content = KanbanPhoneBody.Create(client, ProjectId), Width = 360, Height = 740 };
        window.Show();
        await WaitFor(() => Texts(window).Contains("Ready (1)"));

        Assert.Equal(1, client.LoadCalls);
        Assert.Contains("Ready (1)", Texts(window));
    }

    [AvaloniaFact]
    public void Without_a_project_the_body_says_so_and_sends_nothing()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var window = new Window { Content = KanbanPhoneBody.Create(client, null), Width = 360, Height = 740 };
        window.Show();

        Assert.Equal(0, client.LoadCalls);
        Assert.Equal(KanbanPhoneBody.NoProjectText, Assert.IsType<TextBlock>(window.Content).Text);
    }

    [AvaloniaFact]
    public async Task A_failed_load_shows_the_status_line_and_does_not_throw()
    {
        var client = new FakeKanbanBoardClient { FailLoads = true };
        var window = new Window { Content = KanbanPhoneBody.Create(client, ProjectId), Width = 360, Height = 740 };
        window.Show();
        await WaitFor(() => Texts(window).Contains("Could not load the board."));

        Assert.Contains("Could not load the board.", Texts(window));
    }

    private static List<string> Texts(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToList();

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
