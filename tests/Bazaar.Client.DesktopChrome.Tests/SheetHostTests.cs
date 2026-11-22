using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Bazaar.Client.Motion;

namespace Bazaar.Client.DesktopChrome.Tests;

public class SheetHostTests
{
    private sealed record Setup(ChromeRig Rig, DesktopChromeShell Shell, Window Window, Button Before, Button Inside);

    private static Setup Build(MotionLevel? level = null)
    {
        var rig = new ChromeRig(level);
        var before = new Button { Content = "before" };
        DesktopChromeShell shell = rig.Create(before);
        Window window = ChromeRig.Show(shell);
        before.Focus();
        return new Setup(rig, shell, window, before, new Button { Content = "inside" });
    }

    private static void Press(Window window, Key key) => window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

    [AvaloniaFact]
    public void Show_opens_a_sheet_and_moves_focus_into_it()
    {
        Setup s = Build();

        s.Shell.Sheets.Show(s.Inside, "Details");
        s.Rig.Frames.Settle();

        Assert.True(s.Shell.Sheets.IsOpen);
        Assert.True(s.Shell.Sheets.View.IsVisible);
        Assert.Equal(1, s.Shell.Sheets.OpenProgress);
        Assert.Same(s.Inside, s.Window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void Esc_dismisses_the_sheet_and_focus_returns_where_it_was()
    {
        Setup s = Build();
        int closed = 0;
        s.Shell.Sheets.Closed += (_, _) => closed++;
        s.Shell.Sheets.Show(s.Inside, "Details");
        s.Rig.Frames.Settle();

        Press(s.Window, Key.Escape);
        s.Rig.Frames.Settle();

        Assert.False(s.Shell.Sheets.IsOpen);
        Assert.False(s.Shell.Sheets.View.IsVisible);
        Assert.Equal(1, closed);
        Assert.Same(s.Before, s.Window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void The_close_button_is_keyboard_operable()
    {
        Setup s = Build();
        s.Shell.Sheets.Show(s.Inside, "Details");
        s.Rig.Frames.Settle();
        s.Shell.Sheets.CloseButton.Focus();

        Press(s.Window, Key.Enter);
        s.Rig.Frames.Settle();

        Assert.False(s.Shell.Sheets.View.IsVisible);
    }

    [AvaloniaFact]
    public void Tab_cycles_inside_the_sheet_and_never_reaches_the_content_behind()
    {
        Setup s = Build();
        s.Shell.Sheets.Show(s.Inside, "Details");
        s.Rig.Frames.Settle();
        var seen = new List<object?>();

        for (int i = 0; i < 6; i++)
        {
            Press(s.Window, Key.Tab);
            seen.Add(s.Window.FocusManager!.GetFocusedElement());
        }

        Assert.DoesNotContain(s.Before, seen);
        Assert.Contains(s.Inside, seen);
        Assert.Contains(s.Shell.Sheets.CloseButton, seen);
    }

    [AvaloniaFact]
    public void A_press_on_the_scrim_dismisses()
    {
        Setup s = Build();
        s.Shell.Sheets.Show(s.Inside, "Details", SheetKind.Side);
        s.Rig.Frames.Settle();
        s.Window.UpdateLayout();

        s.Window.MouseDown(new Point(20, 400), MouseButton.Left);
        s.Window.MouseUp(new Point(20, 400), MouseButton.Left);
        s.Rig.Frames.Settle();

        Assert.False(s.Shell.Sheets.View.IsVisible);
    }

    [AvaloniaFact]
    public void Dragging_the_header_past_the_fraction_dismisses_a_side_sheet()
    {
        Setup s = Build();
        s.Shell.Sheets.Show(s.Inside, "Details", SheetKind.Side);
        s.Rig.Frames.Settle();
        s.Window.UpdateLayout();
        Rect panel = s.Shell.Sheets.Panel.Bounds;
        var start = new Point(panel.X + 40, panel.Y + 12);

        s.Window.MouseDown(start, MouseButton.Left);
        s.Window.MouseMove(new Point(start.X + (panel.Width * 0.6), start.Y), RawInputModifiers.LeftMouseButton);
        Assert.InRange(s.Shell.Sheets.DragProgress, 0.5, 0.7);
        s.Window.MouseUp(new Point(start.X + (panel.Width * 0.6), start.Y), MouseButton.Left);
        s.Rig.Frames.Settle();

        Assert.False(s.Shell.Sheets.View.IsVisible);
        Assert.False(s.Shell.Sheets.IsOpen);
    }

    [AvaloniaFact]
    public void A_short_drag_springs_back_and_the_sheet_stays_open()
    {
        Setup s = Build();
        s.Shell.Sheets.Show(s.Inside, "Details", SheetKind.Side);
        s.Rig.Frames.Settle();
        s.Window.UpdateLayout();
        Rect panel = s.Shell.Sheets.Panel.Bounds;
        var start = new Point(panel.X + 40, panel.Y + 12);

        s.Window.MouseDown(start, MouseButton.Left);
        s.Window.MouseMove(new Point(start.X + (panel.Width * 0.1), start.Y), RawInputModifiers.LeftMouseButton);
        s.Window.MouseUp(new Point(start.X + (panel.Width * 0.1), start.Y), MouseButton.Left);
        s.Rig.Frames.Settle();

        Assert.True(s.Shell.Sheets.IsOpen);
        Assert.Equal(0, s.Shell.Sheets.DragProgress);
        Assert.Null(s.Shell.Sheets.Panel.RenderTransform is TranslateTransform { X: not 0 } ? s.Shell.Sheets.Panel.RenderTransform : null);
    }

    [AvaloniaFact]
    public void A_modal_sheet_drags_down_to_dismiss()
    {
        Setup s = Build();
        s.Shell.Sheets.Show(s.Inside, "Details", SheetKind.Modal);
        s.Rig.Frames.Settle();
        s.Window.UpdateLayout();
        Rect panel = s.Shell.Sheets.Panel.Bounds;
        var start = new Point(panel.X + 40, panel.Y + 12);

        s.Window.MouseDown(start, MouseButton.Left);
        s.Window.MouseMove(new Point(start.X, start.Y + (panel.Height * 0.8)), RawInputModifiers.LeftMouseButton);
        s.Window.MouseUp(new Point(start.X, start.Y + (panel.Height * 0.8)), MouseButton.Left);
        s.Rig.Frames.Settle();

        Assert.False(s.Shell.Sheets.View.IsVisible);
    }

    [AvaloniaFact]
    public void Opening_never_overshoots_and_travels_no_further_than_the_motion_token()
    {
        Setup s = Build();
        double travel = s.Rig.Settings.Tokens.SubtleSlide;
        double maxOpen = 0;
        double maxShift = 0;

        s.Shell.Sheets.Show(s.Inside, "Details", SheetKind.Modal);
        while (s.Rig.Frames.Step())
        {
            maxOpen = Math.Max(maxOpen, s.Shell.Sheets.Panel.Opacity);
            if (s.Shell.Sheets.Panel.RenderTransform is TranslateTransform t)
            {
                maxShift = Math.Max(maxShift, Math.Abs(t.Y));
            }
        }

        Assert.True(maxOpen <= 1.0);
        Assert.True(maxShift <= travel + 1e-9);
        Assert.Equal(1, s.Shell.Sheets.Panel.Opacity);
    }

    [AvaloniaFact]
    public void Subtle_open_takes_about_half_the_phone_sheet_duration()
    {
        Setup s = Build();

        s.Shell.Sheets.Show(s.Inside, "Details");
        TimeSpan took = s.Rig.Frames.Settle();

        Assert.InRange(took.TotalMilliseconds, 190, 240);
    }

    [AvaloniaFact]
    public void Reduced_motion_is_a_fade_of_ninety_milliseconds_or_less_with_no_travel()
    {
        Setup s = Build(MotionLevel.Reduced);
        bool moved = false;

        s.Shell.Sheets.Show(s.Inside, "Details", SheetKind.Side);
        TimeSpan took = default;
        TimeSpan start = s.Rig.Frames.Now;
        while (s.Rig.Frames.Step())
        {
            moved |= s.Shell.Sheets.Panel.RenderTransform is not null;
            took = s.Rig.Frames.Now - start;
        }

        Assert.False(moved);
        Assert.True(took <= TimeSpan.FromMilliseconds(90 + 32));
        Assert.Equal(1, s.Shell.Sheets.Panel.Opacity);
    }

    [AvaloniaFact]
    public void Showing_a_second_sheet_replaces_the_first_and_still_returns_to_the_original_focus()
    {
        Setup s = Build();
        var second = new Button();
        s.Shell.Sheets.Show(s.Inside, "First");
        s.Rig.Frames.Settle();

        s.Shell.Sheets.Show(second, "Second", SheetKind.Modal);
        s.Rig.Frames.Settle();
        Assert.Same(second, s.Window.FocusManager!.GetFocusedElement());

        s.Shell.Sheets.Dismiss();
        s.Rig.Frames.Settle();

        Assert.Same(s.Before, s.Window.FocusManager.GetFocusedElement());
    }

    [AvaloniaFact]
    public void Dismiss_with_nothing_open_does_nothing()
    {
        Setup s = Build();

        s.Shell.Sheets.Dismiss();

        Assert.False(s.Shell.Sheets.View.IsVisible);
        Assert.False(s.Rig.Frames.HasPending);
    }

    [AvaloniaFact]
    public void The_panel_is_named_for_its_title_and_the_scrim_and_panel_take_theme_colours()
    {
        Setup s = Build();

        s.Shell.Sheets.Show(s.Inside, "Details");

        Assert.Equal("Details", Avalonia.Automation.AutomationProperties.GetName(s.Shell.Sheets.Panel));
        Assert.IsType<SolidColorBrush>(s.Shell.Sheets.Scrim.Background);
        Assert.NotNull(s.Shell.Sheets.Panel.BorderBrush);
    }
}
