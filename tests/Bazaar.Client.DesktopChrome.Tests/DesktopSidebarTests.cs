using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion;

namespace Bazaar.Client.DesktopChrome.Tests;

public class DesktopSidebarTests
{
    private static void Press(Window window, Key key) => window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

    private static double Token(ChromeRig rig, string name) => new ChromeTheme(rig.Theme).Length(name);

    [AvaloniaFact]
    public void The_surfaces_are_drawn_with_the_chrome_strip_and_sheet_materials()
    {
        var rig = new ChromeRig();

        rig.Create();

        Assert.Equal([MaterialSurface.Chrome, MaterialSurface.Strip, MaterialSurface.Sheet], rig.Surfaces);
    }

    [AvaloniaFact]
    public void Enter_on_the_toggle_collapses_the_sidebar_and_space_expands_it()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        Window window = ChromeRig.Show(shell);
        shell.Sidebar.ToggleButton.Focus();

        Press(window, Key.Enter);
        rig.Frames.Settle();

        Assert.False(shell.Sidebar.IsExpanded);
        Assert.Equal(0, shell.Sidebar.Progress);
        Assert.Equal(shell.Sidebar.View.Width, Token(rig, "layout.sidebar.width.collapsed"));

        // A button takes Space on key-up, like a real keyboard.
        Press(window, Key.Space);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
        rig.Frames.Settle();

        Assert.True(shell.Sidebar.IsExpanded);
        Assert.Equal(Token(rig, "layout.sidebar.width.expanded"), shell.Sidebar.View.Width);
    }

    [AvaloniaFact]
    public void The_toggle_name_follows_the_state_and_the_sidebar_is_named()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        ChromeLabels labels = ChromeLabels.Default;

        Assert.Equal(labels.CollapseSidebar, AutomationProperties.GetName(shell.Sidebar.ToggleButton));
        Assert.Equal(labels.SidebarName, AutomationProperties.GetName(shell.Sidebar.View));

        shell.Sidebar.Toggle();

        Assert.Equal(labels.ExpandSidebar, AutomationProperties.GetName(shell.Sidebar.ToggleButton));
        Assert.False(string.IsNullOrEmpty(AutomationProperties.GetAutomationId(shell.Sidebar.ToggleButton)));
    }

    [AvaloniaFact]
    public void A_collapsed_sidebar_takes_its_list_out_of_the_tab_order()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        var item = new Button();
        shell.Sidebar.ListSlot.Child = item;
        ChromeRig.Show(shell);

        shell.Sidebar.SetExpanded(false);
        rig.Frames.Settle();

        Assert.False(shell.Sidebar.ListSlot.IsVisible);
        Assert.False(item.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Tab_runs_toggle_then_list_then_content()
    {
        var rig = new ChromeRig();
        var listItem = new Button();
        var contentItem = new Button();
        DesktopChromeShell shell = rig.Create(contentItem);
        shell.Sidebar.ListSlot.Child = listItem;
        Window window = ChromeRig.Show(shell);
        shell.Sidebar.ToggleButton.Focus();

        Press(window, Key.Tab);
        Assert.Same(listItem, window.FocusManager!.GetFocusedElement());

        Press(window, Key.Tab);
        Assert.Same(contentItem, window.FocusManager.GetFocusedElement());
    }

    [AvaloniaFact]
    public void Collapse_animates_opacity_and_transform_only_and_snaps_the_width_at_the_end()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        ChromeRig.Show(shell);
        var widths = new HashSet<double>();
        double maxOpacity = 0;

        shell.Sidebar.SetExpanded(false);
        while (rig.Frames.Step())
        {
            widths.Add(shell.Sidebar.View.Width);
            maxOpacity = Math.Max(maxOpacity, shell.Sidebar.ListSlot.Opacity);
            if (shell.Sidebar.Progress > 0)
            {
                Assert.Equal(Token(rig, "layout.sidebar.width.expanded"), shell.Sidebar.View.Width);
            }
        }

        Assert.True(maxOpacity <= 1.0);
        Assert.Equal(0, shell.Sidebar.ListSlot.Opacity);
        Assert.Equal(Token(rig, "layout.sidebar.width.collapsed"), shell.Sidebar.View.Width);
        Assert.True(widths.Count <= 2);
    }

    [AvaloniaFact]
    public void Expand_snaps_the_width_first_then_fades_and_slides_the_list_in_without_overshoot()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        shell.Sidebar.SetExpanded(false);
        rig.Frames.Settle();

        shell.Sidebar.SetExpanded(true);

        Assert.Equal(Token(rig, "layout.sidebar.width.expanded"), shell.Sidebar.View.Width);
        double max = 0;
        double maxShift = 0;
        while (rig.Frames.Step())
        {
            max = Math.Max(max, shell.Sidebar.ListSlot.Opacity);
            if (shell.Sidebar.ListSlot.RenderTransform is Avalonia.Media.TranslateTransform t)
            {
                maxShift = Math.Max(maxShift, Math.Abs(t.X));
            }
        }

        Assert.True(max <= 1.0);
        Assert.True(maxShift <= rig.Settings.Tokens.SubtleSlide + 1e-9);
        Assert.Equal(1, shell.Sidebar.ListSlot.Opacity);
        Assert.Null(shell.Sidebar.ListSlot.RenderTransform);
    }

    [AvaloniaFact]
    public void Reduced_motion_changes_the_rail_at_once_with_no_frames()
    {
        var rig = new ChromeRig(MotionLevel.Reduced);
        DesktopChromeShell shell = rig.Create();

        shell.Sidebar.SetExpanded(false);

        Assert.Equal(0, shell.Sidebar.Progress);
        Assert.False(rig.Frames.HasPending);
    }

    [AvaloniaFact]
    public void Colours_come_from_the_theme_and_follow_a_theme_switch()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        var before = (global::Avalonia.Media.SolidColorBrush)((Border)shell.Sidebar.View).BorderBrush!;

        shell.Theme.Use(Bazaar.Client.Theme.BundledThemes.Load("paper"));
        var after = (global::Avalonia.Media.SolidColorBrush)((Border)shell.Sidebar.View).BorderBrush!;

        Assert.NotEqual(before.Color, after.Color);
    }

    [AvaloniaFact]
    public void Widths_follow_the_theme_tokens_when_a_theme_overrides_them()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        var tokens = rig.Theme.Tokens.ToDictionary(kv => kv.Key, kv => kv.Value);
        tokens["layout.sidebar.width.expanded"] = "300";
        tokens["layout.sidebar.width.collapsed"] = "60";

        shell.Theme.Use(rig.Theme with { Tokens = tokens });
        shell.Sidebar.SetExpanded(false);
        rig.Frames.Settle();

        Assert.Equal(60, shell.Sidebar.View.Width);
        shell.Sidebar.SetExpanded(true);
        Assert.Equal(300, shell.Sidebar.View.Width);
    }
}
