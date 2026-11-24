using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Gestures.Avalonia;

/// <summary>
/// Applies the predictive back transition to two screens (doc 93): the current one scales and shifts, the one beneath grows toward full size,
/// and a release commits or cancels with the Fluid spring. It watches a <see cref="PredictiveBackController"/> and requests animation frames only
/// while the spring runs. The host does not pop anything: it raises <see cref="PopRequested"/> and the navigation owner pops.
/// </summary>
public sealed class PredictiveBackHost : IDisposable
{
    private readonly Control _current;
    private readonly Control? _behind;
    private readonly IFrameDriver _driver;
    private bool _frameRequested;
    private bool _inFrame;
    private TimeSpan? _lastFrame;

    public PredictiveBackHost(Control current, Control? behind, PredictiveBackController controller, IFrameDriver driver)
    {
        _current = current;
        _behind = behind;
        Controller = controller;
        _driver = driver;
        controller.Changed += OnChanged;
        controller.PopRequested += OnPop;
    }

    public PredictiveBackController Controller { get; }

    public event EventHandler? PopRequested;

    public void Dispose()
    {
        Controller.Changed -= OnChanged;
        Controller.PopRequested -= OnPop;
    }

    private void OnPop(object? sender, EventArgs e) => PopRequested?.Invoke(this, e);

    private void OnChanged(object? sender, EventArgs e)
    {
        Apply(Controller.Transform);
        if (Controller.IsActive && !_frameRequested && !_inFrame)
        {
            _frameRequested = true;
            _lastFrame = null;
            _driver.RequestFrame(OnFrame);
        }
    }

    private void OnFrame(TimeSpan timestamp)
    {
        _frameRequested = false;
        var delta = _lastFrame is { } last ? timestamp - last : TimeSpan.Zero;
        _lastFrame = timestamp;
        _inFrame = true;
        var running = Controller.Advance(delta);
        _inFrame = false;
        if (running)
        {
            _frameRequested = true;
            _driver.RequestFrame(OnFrame);
        }
    }

    private void Apply(BackTransform t)
    {
        if (t == BackTransform.Identity)
        {
            _current.RenderTransform = null;
            if (_behind is not null)
            {
                _behind.RenderTransform = null;
            }

            return;
        }

        _current.RenderTransform = new TransformGroup { Children = { new ScaleTransform(t.Scale, t.Scale), new TranslateTransform(t.TranslateX, 0) } };
        if (_behind is not null)
        {
            _behind.RenderTransform = new ScaleTransform(t.BehindScale, t.BehindScale);
        }
    }
}
