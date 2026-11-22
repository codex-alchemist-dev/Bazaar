using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Bazaar.Accessibility.Avalonia.Tests;

public class FocusScopesTests
{
    private static (Window Window, FocusScopes Scopes, Button A, Button B, Button C) Build()
    {
        var a = new Button { Content = "a" };
        var b = new Button { Content = "b" };
        var c = new Button { Content = "c" };
        var window = new Window { Content = new StackPanel { Children = { a, b, c } } };
        window.Show();
        var scopes = new FocusScopes();
        scopes.Register("dashboard", a);
        scopes.Register("chats", b);
        scopes.Register("composer", c);
        return (window, scopes, a, b, c);
    }

    [AvaloniaFact]
    public void Next_cycles_in_order_and_wraps()
    {
        var (_, scopes, a, b, c) = Build();

        Assert.Equal("dashboard", scopes.FocusNext());
        Assert.True(a.IsFocused);
        Assert.Equal("chats", scopes.FocusNext());
        Assert.True(b.IsFocused);
        Assert.Equal("composer", scopes.FocusNext());
        Assert.True(c.IsFocused);
        Assert.Equal("dashboard", scopes.FocusNext());
    }

    [AvaloniaFact]
    public void Previous_wraps_backwards()
    {
        var (_, scopes, _, _, c) = Build();

        Assert.Equal("composer", scopes.FocusPrevious());
        Assert.True(c.IsFocused);
        Assert.Equal("chats", scopes.FocusPrevious());
    }

    [AvaloniaFact]
    public void Skips_hidden_and_disabled_scopes()
    {
        var (_, scopes, a, b, c) = Build();
        b.IsVisible = false;

        Assert.Equal("dashboard", scopes.FocusNext());
        Assert.Equal("composer", scopes.FocusNext());
        c.IsEnabled = false;
        Assert.Equal("dashboard", scopes.FocusNext());
        Assert.True(a.IsFocused);
    }

    [AvaloniaFact]
    public void Focus_by_name_and_replace_in_place()
    {
        var (_, scopes, _, b, _) = Build();

        Assert.True(scopes.FocusScope("chats"));
        Assert.True(b.IsFocused);
        Assert.Equal("chats", scopes.Current);
        Assert.False(scopes.FocusScope("nope"));

        scopes.Register("chats", new Button());
        Assert.Equal(["dashboard", "chats", "composer"], scopes.Names);
    }

    [AvaloniaFact]
    public void Nothing_focusable_returns_null()
    {
        Assert.Null(new FocusScopes().FocusNext());
    }
}
