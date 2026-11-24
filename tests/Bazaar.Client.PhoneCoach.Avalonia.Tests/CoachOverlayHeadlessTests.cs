using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Settings;

namespace Bazaar.Client.PhoneCoach.Avalonia.Tests;

internal sealed class ManualFrames : IFrameDriver
{
    private Action<TimeSpan>? _pending;
    private TimeSpan _now;

    public bool HasPending => _pending is not null;

    public void RequestFrame(Action<TimeSpan> callback) => _pending = callback;

    public void RunToRest()
    {
        for (int i = 0; i < 300 && _pending is not null; i++)
        {
            _now += TimeSpan.FromMilliseconds(16);
            var callback = _pending;
            _pending = null;
            callback(_now);
        }
    }
}

public class CoachOverlayHeadlessTests
{
    private static ClientSettingsStore Store(string? path = null) => new(CoachController.Definitions, path);

    private static CoachOverlay Overlay(CoachController coach, MotionSettings? motion = null, IFrameDriver? frames = null)
    {
        var overlay = new CoachOverlay(coach, motion ?? new MotionSettings(MotionPlatform.Phone), frames);
        new Window { Content = overlay, Width = 360, Height = 740 }.Show();
        return overlay;
    }

    [AvaloniaFact]
    public void Shows_on_a_fresh_install_with_the_swipe_prompt()
    {
        var overlay = Overlay(new CoachController(Store()));

        Assert.True(overlay.IsOpen);
        Assert.Equal(CoachStep.Default[0].Title, overlay.Title);
        Assert.Equal("Next", overlay.PrimaryLabel);
    }

    [AvaloniaFact]
    public void Does_not_show_once_seen()
    {
        var settings = Store();
        settings.TrySetValue(CoachSettingKeys.Seen, "true");

        Assert.False(Overlay(new CoachController(settings)).IsOpen);
    }

    [AvaloniaFact]
    public void Next_moves_to_hold_then_got_it_closes_and_records_seen()
    {
        var settings = Store();
        var overlay = Overlay(new CoachController(settings));

        overlay.PressNext();
        Assert.Equal(CoachStep.Default[1].Title, overlay.Title);
        Assert.Equal("Got it", overlay.PrimaryLabel);

        overlay.PressNext();
        Assert.False(overlay.IsOpen);
        Assert.Equal("true", settings.GetValue(CoachSettingKeys.Seen));
    }

    [AvaloniaFact]
    public void Skip_dismisses_and_the_dismissal_persists_across_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"coach-ui-{Guid.NewGuid():N}.json");
        try
        {
            Overlay(new CoachController(Store(path))).PressSkip();

            Assert.False(Overlay(new CoachController(Store(path))).IsOpen);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Escape_dismisses()
    {
        var coach = new CoachController(Store());
        var overlay = Overlay(coach);

        overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        Assert.False(overlay.IsOpen);
        Assert.True(coach.Seen);
    }

    [AvaloniaFact]
    public void Replay_reopens_the_overlay_from_the_first_prompt()
    {
        var coach = new CoachController(Store());
        var overlay = Overlay(coach);
        overlay.PressNext();
        overlay.PressNext();

        coach.Replay();

        Assert.True(overlay.IsOpen);
        Assert.Equal(CoachStep.Default[0].Title, overlay.Title);
    }

    [AvaloniaFact]
    public void Controls_have_accessible_names()
    {
        var overlay = Overlay(new CoachController(Store()));

        var buttons = overlay.GetVisualDescendantsOf<Button>();

        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(global::Avalonia.Automation.AutomationProperties.GetName(b))));
    }

    [AvaloniaFact]
    public void Reduced_motion_enters_with_a_short_fade_and_no_scale()
    {
        var motion = new MotionSettings(MotionPlatform.Phone) { OsReducedMotion = true };
        var frames = new ManualFrames();
        var coach = new CoachController(Store());
        coach.Dismiss();
        var overlay = Overlay(coach, motion, frames);

        coach.Replay();
        Assert.True(frames.HasPending);
        frames.RunToRest();

        var card = overlay.GetVisualDescendantsOf<Border>().First(b => b.Child is StackPanel);
        Assert.Equal(1, card.Opacity);
        Assert.Null(card.RenderTransform);
    }

    [AvaloniaFact]
    public void Liquid_motion_scales_in_and_settles()
    {
        var motion = new MotionSettings(MotionPlatform.Phone);
        var frames = new ManualFrames();
        var coach = new CoachController(Store());
        coach.Dismiss();
        var overlay = Overlay(coach, motion, frames);

        coach.Replay();
        var card = overlay.GetVisualDescendantsOf<Border>().First(b => b.Child is StackPanel);
        Assert.Equal(0, card.Opacity);
        Assert.NotNull(card.RenderTransform);
        frames.RunToRest();

        Assert.Equal(1, card.Opacity);
        Assert.Null(card.RenderTransform);
    }
}

internal static class VisualExtensions
{
    public static List<T> GetVisualDescendantsOf<T>(this Control root) where T : Control =>
        [.. global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>()];
}
