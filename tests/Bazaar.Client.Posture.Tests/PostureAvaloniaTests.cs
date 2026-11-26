using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Posture.Avalonia;

namespace Bazaar.Client.Posture.Tests;

internal sealed class ManualFrameDriver : IFrameDriver
{
    private Action<TimeSpan>? _pending;
    private TimeSpan _now;

    public int Requests { get; private set; }

    public bool HasPending => _pending is not null;

    public void RequestFrame(Action<TimeSpan> callback)
    {
        Requests++;
        _pending = callback;
    }

    public void Step(TimeSpan delta)
    {
        _now += delta;
        var callback = _pending;
        _pending = null;
        callback?.Invoke(_now);
    }
}

public class PostureAvaloniaTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16);
    private static readonly HingeInset _hinge = new(HingeOrientation.Vertical, 500, 20);

    private static (PosturePanel Panel, Border Chat, Border Pane, FakePostureSource Source, MotionSettings Settings, ManualFrameDriver Frames) Create(
        PostureState? initial = null, MotionPlatform platform = MotionPlatform.Phone)
    {
        var chat = new Border();
        var pane = new Border();
        var panel = new PosturePanel { Width = 1000, Height = 800 };
        panel.Children.Add(chat);
        panel.Children.Add(pane);
        Layout(panel);
        return (panel, chat, pane, new FakePostureSource(initial), new MotionSettings(platform), new ManualFrameDriver());
    }

    private static void Layout(PosturePanel panel, double width = 1000)
    {
        panel.Width = width;
        panel.Measure(new Size(width, 800));
        panel.Arrange(new Rect(0, 0, width, 800));
    }

    [AvaloniaFact]
    public void Attached_properties_inherit_to_descendants()
    {
        var host = new Border();
        var child = new Border();
        host.Child = child;
        var root = new Window { Content = host };
        root.Show();
        PostureLayout.SetHingeInset(host, _hinge);
        PostureLayout.SetLayoutClass(host, PostureLayoutClass.Book);
        Assert.Equal(_hinge, PostureLayout.GetHingeInset(child));
        Assert.Equal(PostureLayoutClass.Book, PostureLayout.GetLayoutClass(child));
    }

    [AvaloniaFact]
    public void Panel_keeps_the_hinge_gap_clear_in_book()
    {
        var (panel, chat, pane, _, _, _) = Create();
        PostureLayout.SetHingeInset(panel, _hinge);
        PostureLayout.SetLayoutClass(panel, PostureLayoutClass.Book);
        panel.TransitionProgress = 1;
        Layout(panel);
        Assert.Equal(new Rect(0, 0, 500, 800), chat.Bounds);
        Assert.Equal(new Rect(520, 0, 480, 800), pane.Bounds);
    }

    [AvaloniaFact]
    public void Panel_stacks_in_tabletop()
    {
        var (panel, chat, pane, _, _, _) = Create();
        PostureLayout.SetHingeInset(panel, new HingeInset(HingeOrientation.Horizontal, 400, 16));
        PostureLayout.SetLayoutClass(panel, PostureLayoutClass.Tabletop);
        panel.TransitionProgress = 1;
        Layout(panel);
        Assert.Equal(new Rect(0, 0, 1000, 400), chat.Bounds);
        Assert.Equal(new Rect(0, 416, 1000, 384), pane.Bounds);
    }

    [AvaloniaFact]
    public void Folded_panel_gives_the_chat_the_whole_window()
    {
        var (panel, chat, pane, _, _, _) = Create();
        Assert.Equal(new Rect(0, 0, 1000, 800), chat.Bounds);
        Assert.Equal(0, pane.Bounds.Width);
    }

    [AvaloniaFact]
    public void Initial_reading_applies_the_layout_without_a_frame_request()
    {
        var (panel, chat, pane, source, settings, frames) = Create(new PostureState(DevicePosture.Book, null, _hinge));
        using var driver = new PostureTransitionDriver(panel, source, settings, frames);
        Layout(panel);
        Assert.Equal(PostureLayoutClass.Book, PostureLayout.GetLayoutClass(panel));
        Assert.Equal(_hinge, PostureLayout.GetHingeInset(panel));
        Assert.Equal(new Rect(520, 0, 480, 800), pane.Bounds);
        Assert.Equal(0, frames.Requests);
        Assert.False(driver.IsRunning);
    }

    [AvaloniaFact]
    public void Unfolding_without_an_angle_plays_and_settles()
    {
        var (panel, _, pane, source, settings, frames) = Create(new PostureState(DevicePosture.Flat, null, HingeInset.None));
        Layout(panel, 400);
        using var driver = new PostureTransitionDriver(panel, source, settings, frames);
        Assert.Equal(PostureLayoutClass.Folded, driver.LastFrame.Class);

        Layout(panel);
        source.Set(new PostureState(DevicePosture.Book, null, _hinge));
        Assert.True(driver.IsRunning);
        Assert.True(frames.HasPending);
        Assert.Equal(0, panel.TransitionProgress);
        Assert.Equal(0, pane.Opacity);

        var sawMid = false;
        for (var i = 0; i < 100 && driver.IsRunning; i++)
        {
            frames.Step(_frame);
            Layout(panel);
            if (panel.TransitionProgress is > 0.2 and < 0.8)
            {
                sawMid = true;
                Assert.NotNull(pane.RenderTransform);
            }
        }

        Assert.True(sawMid);
        Assert.False(driver.IsRunning);
        Assert.False(frames.HasPending);
        Assert.Equal(1, panel.TransitionProgress);
        Assert.Equal(1, pane.Opacity);
        Assert.Null(pane.RenderTransform);
        Assert.Equal(new Rect(520, 0, 480, 800), pane.Bounds);
    }

    [AvaloniaFact]
    public void Fold_angle_drives_the_progress_directly()
    {
        var (panel, _, _, source, settings, frames) = Create(PostureState.Flat);
        Layout(panel, 400);
        using var driver = new PostureTransitionDriver(panel, source, settings, frames);
        Layout(panel);
        source.Set(new PostureState(DevicePosture.Book, 95, _hinge));
        Assert.Equal(0.5, panel.TransitionProgress, 6);
        source.Set(new PostureState(DevicePosture.Book, 120, _hinge));
        Assert.Equal(100.0 / 150, panel.TransitionProgress, 6);
    }

    [AvaloniaFact]
    public void Reduced_motion_cross_fades_in_90_ms_or_less()
    {
        var (panel, _, pane, source, settings, frames) = Create(PostureState.Flat);
        settings.OsReducedMotion = true;
        Layout(panel, 400);
        using var driver = new PostureTransitionDriver(panel, source, settings, frames);
        Layout(panel);
        source.Set(new PostureState(DevicePosture.Book, 95, _hinge));

        var elapsed = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(5);
        var minPanelOpacity = 1.0;
        while (driver.IsRunning)
        {
            frames.Step(step);
            elapsed += step;
            minPanelOpacity = Math.Min(minPanelOpacity, panel.Opacity);
            Assert.True(panel.TransitionProgress is 0 or 1);
            Assert.Null(pane.RenderTransform);
        }

        Assert.True(elapsed <= TimeSpan.FromMilliseconds(95));
        Assert.True(minPanelOpacity < 0.2);
        Assert.Equal(1, panel.Opacity);
        Assert.Equal(1, panel.TransitionProgress);
    }

    [AvaloniaFact]
    public void Missing_variant_falls_back_to_folded_in_the_driver()
    {
        var (panel, _, _, source, settings, frames) = Create(new PostureState(DevicePosture.Book, null, _hinge));
        using var driver = new PostureTransitionDriver(panel, source, settings, frames, () => LayoutVariants.Unfolded);
        Assert.Equal(PostureLayoutClass.Folded, driver.LastFrame.Class);
    }

    [AvaloniaFact]
    public void Dispose_detaches_from_the_source()
    {
        var (panel, _, _, source, settings, frames) = Create(PostureState.Flat);
        Layout(panel, 400);
        var driver = new PostureTransitionDriver(panel, source, settings, frames);
        driver.Dispose();
        source.Set(new PostureState(DevicePosture.Book, null, _hinge));
        Assert.Equal(PostureLayoutClass.Folded, PostureLayout.GetLayoutClass(panel));
        Assert.Equal(0, frames.Requests);
    }
}
