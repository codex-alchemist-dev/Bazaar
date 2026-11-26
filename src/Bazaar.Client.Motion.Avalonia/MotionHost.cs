using Avalonia.Controls;
using Avalonia.Threading;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// The one shared motion state of a running client (doc 93). Views and attached behaviours read <see cref="Settings"/> from here, so
/// they carry no motion numbers of their own. A head sets <see cref="MotionSettings.Setting"/> from <c>client.motion_level</c> and
/// <see cref="MotionSettings.OsReducedMotion"/> from the OS flag (Avalonia does not expose it uniformly), or replaces the whole
/// object with <see cref="Use"/> when its DI container owns one.
/// </summary>
public static class MotionHost
{
    /// <summary>How far an arriving bubble starts from its place sideways, in device-independent pixels (the avatar side).</summary>
    public const double ArrivalOriginDx = -24;

    /// <summary>How far an arriving bubble starts from its place vertically.</summary>
    public const double ArrivalOriginDy = 12;

    /// <summary>How far below its place an own message starts (the composer).</summary>
    public const double OwnOriginDy = 36;

    /// <summary>How far a reaction chip starts from its place, sideways, at the message edge.</summary>
    public const double ReactionOriginDx = -16;

    /// <summary>More new items than this in one layout pass is a bulk load (history), not live arrivals, and is not animated.</summary>
    public const int MaxAnimatedBurst = 3;

    private static readonly Lock _gate = new();
    private static MotionSettings? _settings;
    private static IFrameTimeSink? _frameTimes;

    /// <summary>The shared settings; created on first use with the platform default.</summary>
    public static MotionSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings ??= new MotionSettings(OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? MotionPlatform.Phone : MotionPlatform.Desktop);
            }
        }
    }

    /// <summary>Replaces the shared settings with one the head or its DI container owns. Behaviours attached later use it.</summary>
    public static void Use(MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
        }
    }

    /// <summary>
    /// Where the frames of running animations report their timing, for the frame-budget monitor. Null (the default) measures nothing.
    /// It is read when a driver is created by <see cref="DriverFor"/>, so set it before views attach.
    /// </summary>
    public static IFrameTimeSink? FrameTimes
    {
        get { lock (_gate) { return _frameTimes; } }
        set { lock (_gate) { _frameTimes = value; } }
    }

    /// <summary>A frame driver for <paramref name="control"/> that keeps running before the control has a top level.</summary>
    public static IFrameDriver DriverFor(Control control) => new ResilientFrameDriver(control, FrameTimes);
}

/// <summary>
/// Asks the control's top level for frames; while the control is not in a window yet, it falls back to the UI thread so an animation
/// that started during layout still finishes and never leaves a control half faded.
/// </summary>
internal sealed class ResilientFrameDriver(Control control, IFrameTimeSink? frameTimes = null) : IFrameDriver
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly FrameTimeTracker? _tracker = frameTimes is null ? null : new FrameTimeTracker(frameTimes);

    public void RequestFrame(Action<TimeSpan> callback)
    {
        // Only the top level's frames are real display frames; the UI-thread fallback paces itself and says nothing about cost.
        if (TopLevel.GetTopLevel(control) is { } top)
        {
            top.RequestAnimationFrame(_tracker is null ? callback : timestamp =>
            {
                _tracker.OnFrame(timestamp);
                callback(timestamp);
            });
            return;
        }

        Dispatcher.UIThread.Post(() => callback(_clock.Elapsed), DispatcherPriority.Render);
    }
}
