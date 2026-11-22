using Avalonia.Controls;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Asks for the next animation frame. An animation requests a frame only while it runs, so nothing costs anything at rest (doc 93).
/// The callback receives a timestamp; tests drive animators by hand instead.
/// </summary>
public interface IFrameDriver
{
    void RequestFrame(Action<TimeSpan> callback);
}

/// <summary>Frames from the control's top level (the compositor's animation frame callback).</summary>
public sealed class TopLevelFrameDriver : IFrameDriver
{
    private readonly Control _control;

    public TopLevelFrameDriver(Control control) => _control = control;

    public void RequestFrame(Action<TimeSpan> callback) => TopLevel.GetTopLevel(_control)?.RequestAnimationFrame(callback);
}
