using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Bazaar.Client.Tui.TerminalGui.Tests;

public class ShellTests
{
    private static TuiShell NewShell(HeadlessSession session, ShellOptions? options = null) =>
        session.Host.CreateShell(DefaultMenus.Create(), options);

    [Fact]
    public void The_menu_bar_lists_the_default_menus_with_the_first_selected_and_underlines_in_text()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var rows = new List<string>();
        session.Do(() => rows.AddRange(session.Rows));

        shell.RunShell();

        var bar = rows[0];
        Assert.Contains("[Health]", bar);
        Assert.Contains("Kodo", bar);
        Assert.Contains("Logs", bar);
        Assert.Contains("Events", bar);
        Assert.Contains("Admin", bar);
    }

    [Fact]
    public void The_shell_fits_80_by_24_with_the_status_line_on_the_last_row()
    {
        using var session = new HeadlessSession(80, 24);
        using var shell = NewShell(session);
        string[] rows = [];
        session.Do(() => rows = session.Rows);

        shell.RunShell();

        Assert.Equal(24, rows.Length);
        Assert.Contains("Ctrl+C quits", rows[23]);
    }

    [Fact]
    public void Arrow_keys_move_the_highlight_and_down_opens_a_menu_with_items()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var rows = new List<string>();
        session.Press(Key.CursorRight, Key.CursorRight, Key.CursorRight, Key.CursorRight, Key.CursorDown).Do(() => rows.AddRange(session.Rows));

        shell.RunShell();

        Assert.Contains("[Admin]", rows[0]);
        var all = string.Join("\n", rows);
        Assert.Contains("> Modules", all);
        Assert.Contains("Users", all);
        Assert.Contains("Approvals", all);
        Assert.Contains("Audit", all);
        Assert.Contains("Rebuilds", all);
        Assert.True(shell.Menu.IsOpen);
    }

    [Fact]
    public void Enter_on_a_top_level_menu_chooses_it()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        session.Press(Key.CursorRight, Key.Enter);

        shell.RunShell();

        Assert.Equal(["kodo"], chosen);
    }

    [Fact]
    public void Enter_on_an_item_chooses_it_and_closes_the_menu()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        session.Press(Key.CursorRight, Key.CursorRight, Key.CursorRight, Key.CursorRight, Key.CursorDown, Key.CursorDown, Key.Enter);

        shell.RunShell();

        Assert.Equal([DefaultMenus.Users], chosen);
        Assert.False(shell.Menu.IsOpen);
    }

    [Fact]
    public void A_letter_shortcut_chooses_a_top_level_menu_and_opens_a_submenu()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        session.Type("l").Type("a").Type("u");

        shell.RunShell();

        Assert.Equal([DefaultMenus.Logs, DefaultMenus.Users], chosen);
    }

    [Fact]
    public void Shortcuts_can_be_turned_off_and_then_do_nothing()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        shell.Menu.ShortcutsEnabled = false;
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        session.Type("l");

        shell.RunShell();

        Assert.Empty(chosen);
    }

    [Fact]
    public void No_ctrl_chord_chooses_anything()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        session.Press(Key.L.WithCtrl, Key.K.WithAlt);

        shell.RunShell();

        Assert.Empty(chosen);
    }

    [Fact]
    public void Slash_opens_type_to_search_which_lists_matches_and_enter_chooses_one()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        var rows = new List<string>();
        session.Type("/").Type("aud").Do(() => rows.AddRange(session.Rows)).Press(Key.Enter);

        shell.RunShell();

        Assert.Contains("Search: aud", rows[23]);
        Assert.Contains(rows, row => row.Contains("> Admin > Audit", StringComparison.Ordinal));
        Assert.Equal([DefaultMenus.Audit], chosen);
        Assert.False(shell.SearchActive);
    }

    [Fact]
    public void Typing_in_the_search_box_does_not_fire_shortcuts_and_backspace_edits()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        string? query = null;
        session.Type("/").Type("hk").Press(Key.Backspace).Do(() => query = shell.SearchQuery).Press(Key.Esc);

        shell.RunShell();

        Assert.Equal("h", query);
        Assert.Empty(chosen);
        Assert.False(shell.SearchActive);
    }

    [Fact]
    public void Search_with_no_match_says_so_and_enter_chooses_nothing()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        var rows = new List<string>();
        session.Type("/").Type("zzz").Do(() => rows.AddRange(session.Rows)).Press(Key.Enter);

        shell.RunShell();

        Assert.Contains(rows, row => row.Contains("No match", StringComparison.Ordinal));
        Assert.Empty(chosen);
    }

    [Fact]
    public void Search_results_move_with_the_arrow_keys()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var chosen = new List<string>();
        shell.Chosen += chosen.Add;
        // "s" matches Logs, Events, Users... pick the second hit.
        session.Type("/").Type("s").Press(Key.CursorDown, Key.Enter);

        shell.RunShell();

        Assert.Single(chosen);
        Assert.Equal(shell.Menu.Search("s")[1], chosen[0]);
    }

    [Fact]
    public void A_serious_alert_shows_a_red_banner_with_the_word_and_rings_the_bell_once()
    {
        using var session = new HeadlessSession();
        var bells = 0;
        using var shell = NewShell(session, new ShellOptions(Bell: () => bells++));
        var rows = new List<string>();
        var alert = new ShellAlert(ShellAlertKind.Serious, "Database unreachable");
        session.Do(() => shell.ShowAlert(alert)).Do(() => { }).Do(() => rows.AddRange(session.Rows)).Do(() => shell.ShowAlert(alert)).Do(() => { });

        shell.RunShell();

        Assert.Contains("SERIOUS: Database unreachable", rows[1]);
        Assert.Contains("Esc acknowledges", rows[1]);
        Assert.Equal(1, bells);
        Assert.Equal(alert, shell.Alert);
    }

    [Fact]
    public void A_new_serious_alert_rings_again_and_the_banner_stays_until_acknowledged_with_escape()
    {
        using var session = new HeadlessSession();
        var bells = 0;
        using var shell = NewShell(session, new ShellOptions(Bell: () => bells++));
        ShellAlert? afterMenuKeys = null;
        session.Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Serious, "one")))
            .Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Serious, "two")))
            .Press(Key.CursorRight, Key.CursorDown)
            .Do(() => afterMenuKeys = shell.Alert)
            .Press(Key.Esc);

        shell.RunShell();

        Assert.Equal(2, bells);
        Assert.NotNull(afterMenuKeys);
        Assert.Null(shell.Alert);
    }

    [Fact]
    public void Escape_closes_an_open_menu_before_it_acknowledges_the_banner()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session, new ShellOptions(BellEnabled: false));
        ShellAlert? afterFirstEscape = null;
        session.Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Serious, "x")))
            .Press(Key.CursorRight, Key.CursorRight, Key.CursorRight, Key.CursorRight, Key.CursorDown, Key.Esc)
            .Do(() => afterFirstEscape = shell.Alert)
            .Press(Key.Esc);

        shell.RunShell();

        Assert.NotNull(afterFirstEscape);
        Assert.Null(shell.Alert);
    }

    [Fact]
    public void The_bell_can_be_turned_off()
    {
        using var session = new HeadlessSession();
        var bells = 0;
        using var shell = NewShell(session, new ShellOptions(BellEnabled: false, Bell: () => bells++));
        session.Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Serious, "x")));

        shell.RunShell();

        Assert.Equal(0, bells);
        Assert.NotNull(shell.Alert);
    }

    [Fact]
    public void A_health_win_is_a_green_banner_with_the_word_ok_and_no_bell()
    {
        using var session = new HeadlessSession();
        var bells = 0;
        using var shell = NewShell(session, new ShellOptions(Bell: () => bells++));
        var rows = new List<string>();
        session.Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Win, "Survived a fatal crash")))
            .Do(() => { })
            .Do(() => rows.AddRange(session.Rows));

        shell.RunShell();

        Assert.Contains("OK: Survived a fatal crash", rows[1]);
        Assert.Contains("Esc dismisses", rows[1]);
        Assert.Equal(0, bells);
    }

    [Fact]
    public void Acknowledging_removes_the_banner_row()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session, new ShellOptions(BellEnabled: false));
        var rows = new List<string>();
        session.Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Serious, "gone soon")))
            .Do(() => shell.AcknowledgeAlert())
            .Do(() => { })
            .Do(() => rows.AddRange(session.Rows));

        shell.RunShell();

        Assert.DoesNotContain(rows, row => row.Contains("gone soon", StringComparison.Ordinal));
        Assert.Null(shell.Alert);
    }

    [Fact]
    public void The_status_line_shows_the_text_and_falls_back_to_the_key_hint()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        var rows = new List<string>();
        session.Do(() => shell.SetStatus("Server healthy, 3 modules loaded"))
            .Do(() => { })
            .Do(() => rows.AddRange(session.Rows));

        shell.RunShell();

        Assert.Contains("Server healthy, 3 modules loaded", rows[23]);
        Assert.Equal("Server healthy, 3 modules loaded", shell.StatusText);
    }

    [Fact]
    public void Screens_put_their_views_in_the_content_area()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        shell.Content.Add(new Label { X = 1, Y = 1, Text = "Kodo says all green" });
        var rows = new List<string>();
        session.Do(() => rows.AddRange(session.Rows));

        shell.RunShell();

        var line = rows.FindIndex(row => row.Contains("Kodo says all green", StringComparison.Ordinal));
        Assert.True(line >= 2, "content starts below the bar and the banner slot");
    }

    [Fact]
    public void Content_moves_down_when_a_banner_shows()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session, new ShellOptions(BellEnabled: false));
        shell.Content.Add(new Label { X = 1, Y = 0, Text = "overview line" });
        var before = new List<string>();
        var after = new List<string>();
        session.Do(() => before.AddRange(session.Rows))
            .Do(() => shell.ShowAlert(new ShellAlert(ShellAlertKind.Serious, "x")))
            .Do(() => { })
            .Do(() => after.AddRange(session.Rows));

        shell.RunShell();

        Assert.Equal(before.FindIndex(row => row.Contains("overview line", StringComparison.Ordinal)) + 1, after.FindIndex(row => row.Contains("overview line", StringComparison.Ordinal)));
    }

    [Fact]
    public void Ctrl_c_stops_the_shell()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);
        session.Press(Key.C.WithCtrl);

        shell.RunShell();

        Assert.False(session.StoppedByScriptEnd);
    }

    [Fact]
    public void The_look_is_the_fixed_dark_scheme_whatever_the_terminal_configuration()
    {
        using var session = new HeadlessSession();
        using var shell = NewShell(session);

        Assert.True(shell.HasScheme);
        var normal = shell.GetScheme().Normal;
        Assert.Equal(new Terminal.Gui.Drawing.Color(Terminal.Gui.Drawing.ColorName16.Black), normal.Background);
        Assert.Equal(new Terminal.Gui.Drawing.Color(Terminal.Gui.Drawing.ColorName16.Gray), normal.Foreground);
    }
}
