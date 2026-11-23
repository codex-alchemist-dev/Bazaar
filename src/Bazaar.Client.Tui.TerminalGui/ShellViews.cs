using System.Text;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Terminal.Gui.ViewBase;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>Draws a title with its one-letter shortcut underlined (and written as " (x)" when the letter is not in the title).</summary>
internal static class ShortcutText
{
    /// <summary>The text as written, without any styling: the shortcut is appended in brackets when it is not part of the title.</summary>
    public static string Plain(string title, char? shortcut)
    {
        ArgumentNullException.ThrowIfNull(title);
        return shortcut is { } letter && title.IndexOf(letter, StringComparison.OrdinalIgnoreCase) < 0 ? $"{title} ({letter})" : title;
    }

    /// <summary>Draws at the view's current attribute, with the shortcut letter underlined. Returns the column after the text.</summary>
    public static int Draw(View view, int x, int y, string title, char? shortcut)
    {
        var current = view.GetCurrentAttribute();
        var underlined = new Attribute(current.Foreground, current.Background, current.Style | TextStyle.Underline);
        var index = shortcut is { } letter ? title.IndexOf(letter, StringComparison.OrdinalIgnoreCase) : -1;

        view.Move(x, y);
        if (index < 0)
        {
            view.AddStr(Plain(title, shortcut));
            return x + Plain(title, shortcut).Length;
        }

        view.AddStr(title[..index]);
        view.SetAttribute(underlined);
        view.AddStr(title[index].ToString());
        view.SetAttribute(current);
        view.AddStr(title[(index + 1)..]);
        return x + title.Length;
    }
}

/// <summary>The one-row menu bar. It only draws; <see cref="TuiShell"/> feeds <see cref="MenuBarState"/> the keys.</summary>
internal sealed class MenuBarView : View
{
    private readonly MenuBarState _state;

    public MenuBarView(MenuBarState state)
    {
        _state = state;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = 1;
        CanFocus = false;
        SetScheme(FixedLook.Bar);
    }

    /// <summary>The column where the highlighted menu starts, so the dropdown opens under it.</summary>
    public int ColumnOf(int menuIndex)
    {
        var x = 0;
        for (var index = 0; index < menuIndex; index++)
        {
            x += TitleWidth(_state.Menus[index]);
        }

        return x;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetAttributeForRole(VisualRole.Normal);
        var selected = new Attribute(normal.Background, normal.Foreground, normal.Style);
        SetAttribute(normal);
        FillRect(Viewport, new Rune(' '));

        var x = 0;
        for (var index = 0; index < _state.Menus.Count; index++)
        {
            var menu = _state.Menus[index];
            var current = index == _state.MenuIndex;
            SetAttribute(current ? selected : normal);

            // The selected menu is bracketed as well as inverted, so it reads on a terminal with no colour.
            Move(x, 0);
            AddStr(current ? "[" : " ");
            var end = ShortcutText.Draw(this, x + 1, 0, menu.Title, _state.ShortcutsEnabled ? menu.Shortcut : null);
            Move(end, 0);
            AddStr(current ? "]" : " ");
            x = end + 1;
        }

        SetAttribute(normal);
        return true;
    }

    private int TitleWidth(MenuModel menu) => ShortcutText.Plain(menu.Title, _state.ShortcutsEnabled ? menu.Shortcut : null).Length + 2;
}

/// <summary>One line of the dropdown or of the search results.</summary>
internal sealed record OverlayLine(string Text, char? Shortcut, bool Selected);

/// <summary>The dropdown under the menu bar and the search result list: lines of text with the chosen one marked by "&gt;".</summary>
internal sealed class ListOverlayView : View
{
    private IReadOnlyList<OverlayLine> _lines = [];

    public ListOverlayView()
    {
        X = 0;
        Y = 1;
        Width = 10;
        Height = 1;
        CanFocus = false;
        Visible = false;
        SetScheme(FixedLook.Bar);
    }

    public void SetLines(int column, IReadOnlyList<OverlayLine> lines)
    {
        _lines = lines;
        X = column;
        var width = 12;
        foreach (var line in lines)
        {
            width = Math.Max(width, ShortcutText.Plain(line.Text, line.Shortcut).Length + 4);
        }

        Width = width;
        Height = Math.Max(1, lines.Count);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetAttributeForRole(VisualRole.Normal);
        var selected = new Attribute(normal.Background, normal.Foreground, normal.Style);
        SetAttribute(normal);
        FillRect(Viewport, new Rune(' '));

        for (var row = 0; row < _lines.Count; row++)
        {
            var line = _lines[row];
            SetAttribute(line.Selected ? selected : normal);
            FillRect(new System.Drawing.Rectangle(0, row, Viewport.Width, 1), new Rune(' '));
            Move(0, row);
            AddStr(line.Selected ? "> " : "  ");
            ShortcutText.Draw(this, 2, row, line.Text, line.Shortcut);
        }

        SetAttribute(normal);
        return true;
    }
}
