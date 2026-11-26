using Terminal.Gui.Input;

namespace Bazaar.Client.Tui.TerminalGui.Tests;

public class PromptWidgetsTests
{
    [Fact]
    public void TextEntry_returns_what_was_typed_and_shows_the_prompt()
    {
        using var session = new HeadlessSession();
        var screens = new List<string>();
        session.Type("abc").Capture(screens).Press(Key.Enter);

        var result = session.Host.Widgets.TextEntry("Company name", null);

        Assert.Equal("abc", result);
        Assert.Contains("Company name", screens[0]);
        Assert.Contains("abc", screens[0]);
    }

    [Fact]
    public void TextEntry_with_an_empty_entry_takes_the_default()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Backspace, Key.Backspace, Key.Backspace, Key.Backspace, Key.Backspace, Key.Backspace, Key.Enter);

        Assert.Equal("local", session.Host.Widgets.TextEntry("Instance", "local"));
    }

    [Fact]
    public void TextEntry_with_an_empty_entry_and_no_default_is_empty_text()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Enter);

        Assert.Equal("", session.Host.Widgets.TextEntry("Instance", null));
    }

    [Fact]
    public void TextEntry_is_null_on_escape()
    {
        using var session = new HeadlessSession();
        session.Type("abc").Press(Key.Esc);

        Assert.Null(session.Host.Widgets.TextEntry("Company name", null));
    }

    [Fact]
    public void TextEntry_is_null_on_ctrl_c()
    {
        using var session = new HeadlessSession();
        session.Type("abc").Press(Key.C.WithCtrl);

        Assert.Null(session.Host.Widgets.TextEntry("Company name", null));
    }

    [Fact]
    public void SecretEntry_masks_what_is_typed_and_returns_it_without_echo()
    {
        const string canary = "hunter2-canary";
        using var session = new HeadlessSession();
        var screens = new List<string>();
        session.Type(canary).Capture(screens).Press(Key.Enter).Capture(screens);

        var result = session.Host.Widgets.SecretEntry("Owner passphrase");

        Assert.Equal(canary, result);
        Assert.Contains("Owner passphrase", screens[0]);
        Assert.Contains(new string('\u2219', canary.Length), screens[0]);
        Assert.All(screens, screen => Assert.DoesNotContain(canary, screen));
        Assert.DoesNotContain(canary, session.Screen);
    }

    [Fact]
    public void SecretEntry_is_null_on_escape_and_leaves_nothing_on_screen()
    {
        const string canary = "hunter2-canary";
        using var session = new HeadlessSession();
        session.Type(canary).Press(Key.Esc);

        Assert.Null(session.Host.Widgets.SecretEntry("Owner passphrase"));
        Assert.DoesNotContain(canary, session.Screen);
    }

    [Fact]
    public void PickOne_starts_on_the_default_and_enter_takes_it()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Enter);

        Assert.Equal("beta", session.Host.Widgets.PickOne("Channel", ["alpha", "beta", "stable"], "beta"));
    }

    [Fact]
    public void PickOne_moves_with_the_arrow_keys()
    {
        using var session = new HeadlessSession();
        var screens = new List<string>();
        session.Press(Key.CursorDown).Capture(screens).Press(Key.Enter);

        var result = session.Host.Widgets.PickOne("Channel", ["alpha", "beta", "stable"], null);

        Assert.Equal("beta", result);
        Assert.All(["alpha", "beta", "stable"], option => Assert.Contains(option, screens[0]));
    }

    [Fact]
    public void PickOne_is_null_on_escape_and_with_no_options()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Esc);

        Assert.Null(session.Host.Widgets.PickOne("Channel", ["alpha", "beta"], null));
        Assert.Null(session.Host.Widgets.PickOne("Channel", [], null));
    }

    [Fact]
    public void PickMany_starts_with_the_defaults_marked_and_returns_them_in_listed_order()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Enter);

        var result = session.Host.Widgets.PickMany("Modules", ["a", "b", "c"], ["c", "a"]);

        Assert.Equal(["a", "c"], result);
    }

    [Fact]
    public void PickMany_toggles_with_space()
    {
        using var session = new HeadlessSession();
        // Mark the first, move down, mark the second, then move down and unmark the preset third.
        session.Press(Key.Space, Key.CursorDown, Key.Space, Key.CursorDown, Key.Space, Key.Enter);

        var result = session.Host.Widgets.PickMany("Modules", ["a", "b", "c"], ["c"]);

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void PickMany_may_return_none()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Enter);

        Assert.Empty(session.Host.Widgets.PickMany("Modules", ["a", "b"], [])!);
    }

    [Fact]
    public void PickMany_is_null_on_escape()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Esc);

        Assert.Null(session.Host.Widgets.PickMany("Modules", ["a", "b"], ["a"]));
    }

    [Fact]
    public void YesNo_takes_a_yes_default_on_enter()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Enter);

        Assert.True(session.Host.Widgets.YesNo("Apply the plan?", true));
    }

    [Fact]
    public void YesNo_takes_a_no_default_on_enter()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Enter);

        Assert.False(session.Host.Widgets.YesNo("Apply the plan?", false));
    }

    [Fact]
    public void YesNo_is_null_on_escape()
    {
        using var session = new HeadlessSession();
        session.Press(Key.Esc);

        Assert.Null(session.Host.Widgets.YesNo("Apply the plan?", true));
    }
}
