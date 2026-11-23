using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Bazaar.Accessibility;
using Bazaar.Client.DesktopChrome;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Posture;
using Bazaar.Client.Posture.Avalonia;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Shell.Tests;

public class ShellVisualsTests
{
    private static readonly ThemeDefinition _theme = BundledThemes.Load("graphite");

    private sealed class FixedSource(MaterialEnvironment env) : IMaterialEnvironmentSource
    {
        public MaterialEnvironment Current { get; } = env;
    }

    private sealed class ManualFrames : IFrameDriver
    {
        private Action<TimeSpan>? _pending;
        private TimeSpan _now;

        public void RequestFrame(Action<TimeSpan> callback) => _pending = callback;

        // Runs frames until the transition has nothing left to ask for.
        public void Settle()
        {
            for (var i = 0; i < 200 && _pending is not null; i++)
            {
                _now += TimeSpan.FromMilliseconds(16);
                var callback = _pending;
                _pending = null;
                callback(_now);
            }
        }
    }

    private static readonly MaterialEnvironment _mica = new(MaterialPlatform.Windows, MicaAvailable: true, AcrylicAvailable: true, TransparencyAvailable: true);

    private static (Window Window, ShellVisuals Visuals, Border Chat, Border Pane) Build(MaterialEnvironment env, double width, IPostureSource? posture = null, ManualFrames? frames = null)
    {
        var window = new Window { Width = width, Height = 800 };
        var chat = new Border();
        var pane = new Border();
        var visuals = ShellVisuals.Install(window, chat, pane, new FixedSource(env), posture ?? new FlatPostureSource(), _theme, new MotionSettings(MotionPlatform.Desktop), frames ?? new ManualFrames());
        window.Show();
        return (window, visuals, chat, pane);
    }

    [AvaloniaFact]
    public void The_window_chrome_gets_the_material_hint_and_tint()
    {
        var (window, _, _, _) = Build(_mica, 1000);

        Assert.Equal(WindowTransparencyLevel.Mica, window.TransparencyLevelHint[0]);
        var brush = Assert.IsType<SolidColorBrush>(window.Background);
        Assert.True(brush.Color.A < 255);
    }

    [AvaloniaFact]
    public void Reduce_transparency_gives_an_opaque_window()
    {
        var (window, _, _, _) = Build(_mica with { ReduceTransparency = true }, 1000);

        Assert.Equal(WindowTransparencyLevel.None, window.TransparencyLevelHint[0]);
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
    }

    [AvaloniaFact]
    public void Sidebar_strip_and_sheet_surfaces_take_the_material_and_content_surfaces_are_refused()
    {
        var (_, visuals, _, _) = Build(_mica, 1000);

        foreach (MaterialSurface surface in new[] { MaterialSurface.Chrome, MaterialSurface.Strip, MaterialSurface.Sheet })
        {
            Border host = visuals.Materials.Surface(surface, new TextBlock());
            Assert.True(Assert.IsType<SolidColorBrush>(host.Background).Color.A < 255, surface.ToString());
        }

        foreach (MaterialSurface content in new[] { MaterialSurface.MessageList, MaterialSurface.Editor, MaterialSurface.Terminal, MaterialSurface.Diff, MaterialSurface.LongText })
        {
            Assert.Throws<ArgumentException>(() => visuals.Materials.Surface(content, new TextBlock()));
        }
    }

    [AvaloniaFact]
    public void The_chat_and_pane_are_never_wrapped_in_a_material()
    {
        var (window, visuals, chat, pane) = Build(_mica, 1000);

        Assert.Same(visuals.Posture.Panel, window.Content);
        Assert.Same(chat, visuals.Posture.ChatSlot.Content);
        Assert.Same(pane, visuals.Posture.PaneSlot.Content);
        Assert.Null(chat.Background);
        Assert.Null(pane.Background);
    }

    [AvaloniaFact]
    public void Refresh_redraws_surfaces_when_the_theme_changes()
    {
        var (_, visuals, _, _) = Build(_mica, 1000);
        Border strip = visuals.Materials.Surface(MaterialSurface.Strip, null);
        IBrush? before = strip.Background;

        visuals.Materials.Theme = BundledThemes.Load("paper");

        Assert.NotEqual(((SolidColorBrush)before!).Color, ((SolidColorBrush)strip.Background!).Color);
    }

    [AvaloniaFact]
    public void A_flat_window_of_600_dp_or_more_is_dual_pane()
    {
        var frames = new ManualFrames();
        var (window, visuals, chat, pane) = Build(_mica, 1000, frames: frames);
        frames.Settle();
        window.UpdateLayout();

        Assert.Equal(PostureLayoutClass.DualPane, visuals.Posture.LastFrame.Class);
        Assert.True(chat.Bounds.Width > 0 && pane.Bounds.Width > 0);
        Assert.True(chat.Bounds.Width + pane.Bounds.Width <= 1000);
    }

    [AvaloniaFact]
    public void A_flat_window_under_600_dp_is_a_single_chat_pane()
    {
        var frames = new ManualFrames();
        var (window, visuals, chat, pane) = Build(_mica, 500, frames: frames);
        frames.Settle();
        window.UpdateLayout();

        Assert.Equal(PostureLayoutClass.Folded, visuals.Posture.LastFrame.Class);
        Assert.Equal(500, chat.Bounds.Width);
        Assert.Equal(0, pane.Bounds.Width);
    }

    [AvaloniaFact]
    public void A_half_open_reading_changes_the_layout_through_the_posture_source()
    {
        var frames = new ManualFrames();
        var source = new FakePostureSource();
        var (window, visuals, _, _) = Build(_mica, 500, source, frames);
        frames.Settle();

        source.Set(new PostureState(DevicePosture.Book, null, new HingeInset(HingeOrientation.Vertical, 240, 20)));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        frames.Settle();
        window.UpdateLayout();

        Assert.Equal(PostureLayoutClass.Book, visuals.Posture.LastFrame.Class);
    }

    [AvaloniaFact]
    public void The_desktop_chrome_wraps_the_posture_layout_and_draws_with_the_chrome_materials()
    {
        var (window, visuals, chat, pane) = Build(_mica, 1000);

        DesktopChromeShell chrome = visuals.MountDesktopChrome(window, _theme, new ManualFrames());

        Assert.Same(chrome.View, window.Content);
        Assert.Same(visuals.Posture.Panel, chrome.ContentSlot.Child);
        Assert.Same(chat, visuals.Posture.ChatSlot.Content);
        Assert.Same(pane, visuals.Posture.PaneSlot.Content);
        foreach (Control surface in new Control[] { chrome.Sidebar.View, chrome.Strip.View, chrome.Sheets.Panel })
        {
            Assert.True(Assert.IsType<SolidColorBrush>(((Border)surface).Background).Color.A < 255);
        }

        Assert.Null(chrome.ContentSlot.Background);
    }

    [AvaloniaFact]
    public void The_desktop_chrome_falls_back_to_opaque_with_reduce_transparency_and_follows_a_theme_switch()
    {
        var (window, visuals, _, _) = Build(_mica with { ReduceTransparency = true }, 1000);
        DesktopChromeShell chrome = visuals.MountDesktopChrome(window, _theme, new ManualFrames());
        var sidebar = (Border)chrome.Sidebar.View;
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(sidebar.Background).Color.A);
        Color before = ((SolidColorBrush)sidebar.Background!).Color;

        visuals.ApplyTheme(BundledThemes.Load("paper"));

        Assert.NotEqual(before, ((SolidColorBrush)sidebar.Background!).Color);
    }

    [AvaloniaFact]
    public void The_desktop_chrome_mounts_once()
    {
        var (window, visuals, _, _) = Build(_mica, 1000);
        visuals.MountDesktopChrome(window, _theme, new ManualFrames());

        Assert.Throws<InvalidOperationException>(() => visuals.MountDesktopChrome(window, _theme, new ManualFrames()));
    }

    [AvaloniaFact]
    public void A_sheet_opened_through_the_shell_chrome_closes_with_escape()
    {
        var frames = new ManualFrames();
        var (window, visuals, _, _) = Build(_mica, 1000, frames: frames);
        DesktopChromeShell chrome = visuals.MountDesktopChrome(window, _theme, frames);
        var inside = new Button();

        chrome.Sheets.Show(inside, "Details");
        frames.Settle();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        frames.Settle();

        Assert.False(chrome.Sheets.View.IsVisible);
    }

    private sealed class RecordingAnnouncer : IShellAnnouncer
    {
        public List<(AnnouncementCategory Category, string Text)> Spoken { get; } = [];

        public bool Announce(AnnouncementCategory category, string text)
        {
            Spoken.Add((category, text));
            return true;
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sheet_open_and_close_go_through_the_shell_announcer_whichever_is_set_first(bool announcerFirst)
    {
        var frames = new ManualFrames();
        var (window, visuals, _, _) = Build(_mica, 1000, frames: frames);
        var announcer = new RecordingAnnouncer();
        if (announcerFirst)
        {
            visuals.AnnounceThrough(announcer);
        }

        DesktopChromeShell chrome = visuals.MountDesktopChrome(window, _theme, frames);
        if (!announcerFirst)
        {
            visuals.AnnounceThrough(announcer);
        }

        chrome.Sheets.Show(new Button(), "Details");
        frames.Settle();
        chrome.Sheets.Dismiss();
        frames.Settle();

        Assert.Equal(2, announcer.Spoken.Count);
        Assert.All(announcer.Spoken, a => Assert.Equal(AnnouncementCategory.Dialog, a.Category));
    }

    [Fact]
    public void The_dialog_category_is_not_rate_limited()
    {
        Assert.Equal(TimeSpan.Zero, AnnouncementCategorySettings.Default(AnnouncementCategory.Dialog).MinimumInterval);
    }

    [AvaloniaFact]
    public void Chrome_falls_back_to_opaque_when_the_window_system_grants_no_backdrop()
    {
        var feedback = new FeedbackSource();
        var window = new Window { Width = 800, Height = 600 };
        var materials = new ShellMaterials(new MaterialPolicy(feedback), _theme, feedback);
        materials.AttachWindow(window);
        window.Show();

        // The headless platform grants no translucency, which is exactly the refused-backdrop case.
        Assert.Equal(WindowTransparencyLevel.None, window.ActualTransparencyLevel);
        Assert.True(feedback.Failed);
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
    }

    private sealed class FeedbackSource : IMaterialEnvironmentSource, IMaterialFeedback
    {
        public bool Failed { get; private set; }

        public MaterialEnvironment Current => _mica with { BackdropRequestFailed = Failed };

        public void ReportBackdropFailed() => Failed = true;
    }
}
