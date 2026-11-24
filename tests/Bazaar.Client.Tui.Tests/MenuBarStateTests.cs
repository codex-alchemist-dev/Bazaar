namespace Bazaar.Client.Tui.Tests;

public sealed class MenuBarStateTests
{
    private static TerminalKey Key(TerminalKeyKind kind) => new(kind);

    [Fact]
    public void The_default_bar_has_health_kodo_logs_events_and_admin_with_the_admin_screens_under_it()
    {
        var menus = DefaultMenus.Create();

        Assert.Equal(["Health", "Kodo", "Logs", "Events", "Admin"], menus.Select(menu => menu.Title));
        Assert.Equal(["Modules", "Users", "Approvals", "Audit", "Rebuilds"], menus[4].Items.Select(item => item.Title));
    }

    [Fact]
    public void Enter_on_a_menu_without_items_activates_it()
    {
        var bar = new MenuBarState(DefaultMenus.Create());

        Assert.Equal(DefaultMenus.Health, bar.Handle(Key(TerminalKeyKind.Enter)));
    }

    [Fact]
    public void Arrow_keys_move_the_highlight_and_wrap()
    {
        var bar = new MenuBarState(DefaultMenus.Create());

        bar.Handle(Key(TerminalKeyKind.Left));
        Assert.Equal(4, bar.MenuIndex);
        bar.Handle(Key(TerminalKeyKind.Right));
        Assert.Equal(0, bar.MenuIndex);
    }

    [Fact]
    public void Down_opens_the_admin_menu_and_enter_chooses_the_highlighted_item()
    {
        var bar = new MenuBarState(DefaultMenus.Create());
        bar.Handle(Key(TerminalKeyKind.Left));

        Assert.Null(bar.Handle(Key(TerminalKeyKind.Down)));
        Assert.True(bar.IsOpen);
        bar.Handle(Key(TerminalKeyKind.Down));

        Assert.Equal(DefaultMenus.Users, bar.Handle(Key(TerminalKeyKind.Enter)));
        Assert.False(bar.IsOpen);
    }

    [Fact]
    public void Escape_closes_an_open_menu()
    {
        var bar = new MenuBarState(DefaultMenus.Create());
        bar.Handle(Key(TerminalKeyKind.Left));
        bar.Handle(Key(TerminalKeyKind.Down));

        bar.Handle(Key(TerminalKeyKind.Escape));

        Assert.False(bar.IsOpen);
    }

    [Fact]
    public void A_letter_shortcut_reaches_a_screen_and_opens_a_menu_with_items()
    {
        var bar = new MenuBarState(DefaultMenus.Create());

        Assert.Equal(DefaultMenus.Logs, bar.Handle(TerminalKey.Of('L')));
        Assert.Null(bar.Handle(TerminalKey.Of('a')));
        Assert.True(bar.IsOpen);
        Assert.Equal(DefaultMenus.Audit, bar.Handle(TerminalKey.Of('d')));
    }

    [Fact]
    public void Shortcuts_can_be_turned_off_and_the_bar_still_works_by_arrow_keys()
    {
        var bar = new MenuBarState(DefaultMenus.Create()) { ShortcutsEnabled = false };

        Assert.Null(bar.Handle(TerminalKey.Of('l')));
        bar.Handle(Key(TerminalKeyKind.Right));
        Assert.Equal(DefaultMenus.Kodo, bar.Handle(Key(TerminalKeyKind.Enter)));
    }

    [Fact]
    public void Type_to_search_finds_entries_across_menus_by_title()
    {
        var bar = new MenuBarState(DefaultMenus.Create());

        Assert.Equal([DefaultMenus.Users], bar.Search("use"));
        Assert.Equal([DefaultMenus.Events], bar.Search("EVENT"));
        Assert.Empty(bar.Search("  "));
    }

    [Fact]
    public void A_bar_with_no_menus_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MenuBarState([]));
    }
}
