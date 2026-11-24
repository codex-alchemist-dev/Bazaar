using Avalonia;
using Avalonia.Controls;
using Bazaar.Client.Materials;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Theme;

namespace Bazaar.Client.DesktopChrome.Tests;

internal sealed class ManualFrames : IFrameDriver
{
    private Action<TimeSpan>? _pending;

    public TimeSpan Now { get; private set; }

    public bool HasPending => _pending is not null;

    public void RequestFrame(Action<TimeSpan> callback) => _pending = callback;

    /// <summary>Runs one frame, 16 ms after the last. Returns false when nothing was waiting.</summary>
    public bool Step()
    {
        if (_pending is null)
        {
            return false;
        }

        Now += TimeSpan.FromMilliseconds(16);
        Action<TimeSpan> callback = _pending;
        _pending = null;
        callback(Now);
        return true;
    }

    /// <summary>Runs frames until the animation asks for no more; returns how long they ran.</summary>
    public TimeSpan Settle()
    {
        TimeSpan start = Now;
        for (int i = 0; i < 1000 && Step(); i++)
        {
        }

        return Now - start;
    }
}

internal sealed class ChromeRig
{
    public ChromeRig(MotionLevel? level = null, bool osReduced = false, string theme = "graphite")
    {
        Theme = BundledThemes.Load(theme);
        Settings = new MotionSettings(MotionPlatform.Desktop) { Setting = level, OsReducedMotion = osReduced };
        Frames = new ManualFrames();
        Surfaces = [];
    }

    public ThemeDefinition Theme { get; }

    public MotionSettings Settings { get; }

    public ManualFrames Frames { get; }

    public List<MaterialSurface> Surfaces { get; }

    public ChromeSurface Surface => (surface, content) =>
    {
        Surfaces.Add(surface);
        return new Border { Child = content };
    };

    public DesktopChromeShell Create(Control? content = null) => DesktopChromeShell.Create(Surface, Theme, Settings, Frames, content);

    public static Window Show(DesktopChromeShell shell)
    {
        var window = new Window { Width = 1000, Height = 700, Content = shell.View };
        window.Show();
        window.UpdateLayout();
        return window;
    }
}
