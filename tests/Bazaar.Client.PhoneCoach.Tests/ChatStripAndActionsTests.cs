namespace Bazaar.Client.PhoneCoach.Tests;

public class ChatStripAndActionsTests
{
    private static readonly string[] Names = ["General", "Kanban", "Review"];

    [Fact]
    public void Middle_page_has_both_neighbours_and_a_position_description()
    {
        var state = ChatStrip.From(Names, 1);

        Assert.Equal("Kanban", state.Current);
        Assert.Equal("General", state.Previous);
        Assert.Equal("Review", state.Next);
        Assert.True(state.HasNeighbours);
        Assert.Equal("Kanban, chat 2 of 3", state.Description);
    }

    [Fact]
    public void Ends_have_only_one_neighbour()
    {
        Assert.Null(ChatStrip.From(Names, 0).Previous);
        Assert.Equal("Kanban", ChatStrip.From(Names, 0).Next);
        Assert.Null(ChatStrip.From(Names, 2).Next);
    }

    [Fact]
    public void Single_page_has_no_neighbours_and_no_hint()
    {
        Assert.False(ChatStrip.From(["Only"], 0).HasNeighbours);
    }

    [Fact]
    public void Empty_or_out_of_range_selects_nothing()
    {
        Assert.Same(ChatStripState.Empty, ChatStrip.From([], 0));
        var state = ChatStrip.From(Names, 9);
        Assert.Equal(-1, state.Selected);
        Assert.Equal("", state.Current);
        Assert.Equal("", state.Description);
    }

    private sealed class FakeNavigator(int selected, int count) : IPagerNavigator
    {
        public int SelectedPage { get; private set; } = selected;

        public int PageCount { get; } = count;

        public bool MenuOpened { get; private set; }

        public bool GoToNext() => SelectedPage + 1 < PageCount && ++SelectedPage >= 0;

        public bool GoToPrevious() => SelectedPage > 0 && --SelectedPage >= 0;

        public bool OpenMenu() => MenuOpened = true;
    }

    [Fact]
    public void Actions_are_next_previous_and_menu_with_spoken_labels()
    {
        var actions = PagerAccessibility.Build(new FakeNavigator(1, 3));

        Assert.Equal([PagerActionIds.NextChat, PagerActionIds.PreviousChat, PagerActionIds.OpenMenu], actions.Select(a => a.Id).ToArray());
        Assert.Equal(["Next chat", "Previous chat", "Open menu"], actions.Select(a => a.Label).ToArray());
        Assert.All(actions, a => Assert.True(a.Enabled));
    }

    [Fact]
    public void Edge_pages_disable_the_action_that_has_nowhere_to_go()
    {
        var first = PagerAccessibility.Build(new FakeNavigator(0, 3));
        var last = PagerAccessibility.Build(new FakeNavigator(2, 3));

        Assert.False(first.Single(a => a.Id == PagerActionIds.PreviousChat).Enabled);
        Assert.True(first.Single(a => a.Id == PagerActionIds.NextChat).Enabled);
        Assert.False(last.Single(a => a.Id == PagerActionIds.NextChat).Enabled);
        Assert.False(PagerAccessibility.Build(new FakeNavigator(-1, 0)).Single(a => a.Id == PagerActionIds.NextChat).Enabled);
    }

    [Fact]
    public void Invoking_an_action_drives_the_navigator()
    {
        var nav = new FakeNavigator(0, 3);
        var actions = PagerAccessibility.Build(nav);

        Assert.True(actions[0].Invoke());
        Assert.Equal(1, nav.SelectedPage);
        Assert.True(actions[1].Invoke());
        Assert.False(actions[1].Invoke());
        Assert.True(actions[2].Invoke());
        Assert.True(nav.MenuOpened);
    }
}
