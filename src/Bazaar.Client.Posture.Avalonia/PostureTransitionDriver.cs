using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Posture.Avalonia;

/// <summary>
/// Plays the unfold and fold transition (doc 93) on a <see cref="PosturePanel"/>. It reads the <see cref="IPostureSource"/>, picks the layout
/// from the variants the company has, and drives <see cref="PostureTransitionController"/>: tracking the fold angle where the source gives
/// one, a timed spring otherwise, a 90 ms cross-fade at the reduced level. Only opacity and transform change on the second pane; the
/// panel's progress moves the layout. A frame is requested only while a transition runs. Controls that need the chat-name strip and
/// pager channels read <see cref="LastFrame"/> or <see cref="FrameApplied"/>.
/// </summary>
public sealed class PostureTransitionDriver : IDisposable
{
    private readonly PosturePanel _panel;
    private readonly IPostureSource _source;
    private readonly MotionSettings _settings;
    private readonly IFrameDriver _frames;
    private readonly Func<LayoutVariants> _variants;
    private readonly PostureTransitionController _controller;
    private readonly double _slideFraction;

    private TimeSpan? _lastTimestamp;
    private int _generation;
    private bool _disposed;

    public PostureTransitionDriver(
        PosturePanel panel,
        IPostureSource source,
        MotionSettings settings,
        IFrameDriver frames,
        Func<LayoutVariants>? variants = null,
        PostureThresholds? thresholds = null,
        PostureMotionTokens? tokens = null)
    {
        _panel = panel;
        _source = source;
        _settings = settings;
        _frames = frames;
        _variants = variants ?? (() => LayoutVariants.All);
        _controller = new PostureTransitionController(settings.Effective, thresholds, tokens, settings.Tokens);
        _slideFraction = (tokens ?? PostureMotionTokens.Default).Clamped().SlideFraction;
        panel.SplitRatio = (thresholds ?? PostureThresholds.Default).Clamped().SplitRatio;

        _source.Changed += OnSourceChanged;
        _settings.Changed += OnSettingsChanged;
        _panel.PropertyChanged += OnPanelPropertyChanged;
        Refresh();
    }

    /// <summary>The last frame applied.</summary>
    public PostureFrame LastFrame { get; private set; }

    public bool IsRunning => _controller.IsRunning;

    /// <summary>Raised after each frame is applied.</summary>
    public event EventHandler<PostureFrame>? FrameApplied;

    /// <summary>Reads the source and the window width again and starts a transition if the layout changed. Call after the variants change.</summary>
    public void Refresh()
    {
        _controller.Update(_source.Current, _panel.Bounds.Width, _variants());
        Apply(_controller.Current);
        RequestFrameIfRunning();
    }

    /// <summary>Moves the transition forward by hand; frames from the driver call this. Public so tests need no clock.</summary>
    public void Advance(TimeSpan delta) => Apply(_controller.Advance(delta));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        _source.Changed -= OnSourceChanged;
        _settings.Changed -= OnSettingsChanged;
        _panel.PropertyChanged -= OnPanelPropertyChanged;
    }

    private void OnSourceChanged(object? sender, PostureState state)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _controller.SetLevel(_settings.Effective);
        Apply(_controller.Current);
    }

    private void OnPanelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && e.OldValue is Rect old && e.NewValue is Rect now && old.Width != now.Width)
        {
            Refresh();
        }
    }

    private void RequestFrameIfRunning()
    {
        if (_disposed || !_controller.IsRunning)
        {
            return;
        }

        _lastTimestamp = null;
        var generation = ++_generation;
        _frames.RequestFrame(ts => Tick(ts, generation));
    }

    private void Tick(TimeSpan timestamp, int generation)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        var delta = _lastTimestamp is { } last ? timestamp - last : TimeSpan.Zero;
        _lastTimestamp = timestamp;
        Advance(delta);
        if (_controller.IsRunning)
        {
            _frames.RequestFrame(ts => Tick(ts, generation));
        }
    }

    private void Apply(PostureFrame frame)
    {
        LastFrame = frame;
        PostureLayout.SetLayoutClass(_panel, frame.Class);
        PostureLayout.SetHingeInset(_panel, frame.Hinge);
        _panel.TransitionProgress = frame.Unfold.Progress;
        _panel.Opacity = frame.Unfold.ContentOpacity;

        if (_panel.Children.Count > 1)
        {
            var second = _panel.Children[1];
            var settled = frame.Unfold.NewPaneOffset <= 0 && frame.Unfold.NewPaneOpacity >= 1;
            second.Opacity = frame.Unfold.NewPaneOpacity;
            if (settled || frame.Unfold.NewPaneOffset <= 0)
            {
                second.RenderTransform = null;
            }
            else
            {
                var region = _panel.CurrentRegions(_panel.Bounds.Width, _panel.Bounds.Height).Second;
                var travel = frame.Unfold.NewPaneOffset * _slideFraction;
                second.RenderTransform = frame.Class == PostureLayoutClass.Tabletop
                    ? new TranslateTransform(0, -travel * region.Height)
                    : new TranslateTransform(-travel * region.Width, 0);
            }
        }

        FrameApplied?.Invoke(this, frame);
    }
}
