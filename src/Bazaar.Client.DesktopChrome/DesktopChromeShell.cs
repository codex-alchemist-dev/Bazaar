using Avalonia.Controls;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Theme;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// The desktop window layout (doc 93, Desktop and phone split): the sidebar on the leading side, the top strip above the content, and
/// the sheet host over everything. The content slot holds the posture layout (chat and pane) and is never wrapped in a material.
/// Children are added in reading order, so Tab runs sidebar, strip, content, then the sheet when one is open; while a sheet is open the first three are inert and hidden from the accessibility tree.
/// </summary>
public sealed class DesktopChromeShell
{
    private DesktopChromeShell(Grid root, ChromeTheme theme, DesktopSidebar sidebar, DesktopTopStrip strip, SheetHost sheets, Border content)
    {
        View = root;
        Theme = theme;
        Sidebar = sidebar;
        Strip = strip;
        Sheets = sheets;
        ContentSlot = content;
    }

    /// <summary>The control to set as the window's content.</summary>
    public Control View { get; }

    /// <summary>The theme the chrome is drawn with; <see cref="ChromeTheme.Use"/> re-skins it.</summary>
    public ChromeTheme Theme { get; }

    public DesktopSidebar Sidebar { get; }

    public DesktopTopStrip Strip { get; }

    public SheetHost Sheets { get; }

    /// <summary>Where the window's real content goes (the posture panel).</summary>
    public Border ContentSlot { get; }

    /// <summary>
    /// Renames every control from <paramref name="source"/> now and after each locale change. The chrome lives as long as the window,
    /// so there is no unsubscribe.
    /// </summary>
    public void Follow(ChromeLabelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        UseLabels(source.Labels);
        source.Changed += (_, _) => UseLabels(source.Labels);
    }

    /// <summary>Renames every control with <paramref name="labels"/>.</summary>
    public void UseLabels(ChromeLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        Sidebar.Labels = labels;
        Strip.Labels = labels;
        Sheets.Labels = labels;
    }

    public static DesktopChromeShell Create(
        ChromeSurface surface,
        ThemeDefinition theme,
        MotionSettings motion,
        IFrameDriver frames,
        Control? content = null,
        ChromeLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(frames);

        var chromeTheme = new ChromeTheme(theme);
        var chromeMotion = new ChromeMotion(chromeTheme, motion);
        var sidebar = new DesktopSidebar(surface, chromeTheme, chromeMotion, frames, labels);
        var strip = new DesktopTopStrip(surface, chromeTheme, chromeMotion, frames, labels);
        var sheets = new SheetHost(surface, chromeTheme, chromeMotion, frames, labels);
        var contentSlot = new Border { Child = content };

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(strip.View, 0);
        Grid.SetRow(contentSlot, 1);
        main.Children.Add(strip.View);
        main.Children.Add(contentSlot);

        sheets.Background.Add(sidebar.View);
        sheets.Background.Add(strip.View);
        sheets.Background.Add(contentSlot);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(sidebar.View, 0);
        Grid.SetColumn(main, 1);
        Grid.SetColumnSpan(sheets.View, 2);
        root.Children.Add(sidebar.View);
        root.Children.Add(main);
        root.Children.Add(sheets.View);

        return new DesktopChromeShell(root, chromeTheme, sidebar, strip, sheets, contentSlot);
    }
}
