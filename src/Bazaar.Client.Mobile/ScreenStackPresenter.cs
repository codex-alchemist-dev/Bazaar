using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Client.BackStack.Abstractions;
using Bazaar.Client.Gestures;
using Bazaar.Client.Gestures.Avalonia;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Mobile;

/// <summary>
/// Shows the screens of an <see cref="IScreenHistory"/> over a root view and mounts <see cref="PredictiveBackHost"/> over the top two, so a back
/// swipe scales the current screen away and shows the one beneath (doc 93, "Predictive back"). The root view is the stack's root entry (the
/// chat pager); every pushed entry gets a view from the factory, kept while it stays on the stack so its state survives a push on top.
/// Back from the system (<see cref="IBackGestureSource"/>) commits through the host, which pops the stack. At the root there is no host,
/// so the head's back callback should be off (see <see cref="IScreenHistory.CanPop"/>) and the system leaves the app.
/// </summary>
public sealed class ScreenStackPresenter : IDisposable
{
    private const double _fallbackWidth = 400;

    private readonly Panel _host;
    private readonly Control _root;
    private readonly IScreenHistory _stack;
    private readonly Func<ScreenEntry, Control?> _factory;
    private readonly IBackGestureSource? _source;
    private readonly IFrameDriver _frames;
    private readonly MotionSettings _motion;
    private readonly GestureSettings _settings;
    private readonly IBrush _background;
    private readonly List<(ScreenEntry Entry, Control Wrapper)> _views = [];
    private PredictiveBackController? _controller;
    private PredictiveBackHost? _backHost;

    /// <param name="host">The panel the screens are added to. The root view must already be its child.</param>
    /// <param name="root">The view of the stack's root entry.</param>
    /// <param name="stack">The back stack.</param>
    /// <param name="factory">Returns the view for a pushed entry, or null for a kind-and-key placeholder.</param>
    /// <param name="source">Where back-gesture progress comes from; null means no system back is wired and pops come from <see cref="IScreenHistory"/> only.</param>
    /// <param name="frames">Animation frames for the commit and cancel spring.</param>
    /// <param name="motion">Motion level; at the reduced level back is an instant pop with no scaling.</param>
    /// <param name="settings">Predictive back thresholds; null uses the doc 93 defaults.</param>
    /// <param name="background">Fill of a pushed screen, so the one beneath does not show through at rest. Null uses black until the theme supplies one.</param>
    public ScreenStackPresenter(
        Panel host,
        Control root,
        IScreenHistory stack,
        Func<ScreenEntry, Control?> factory,
        IBackGestureSource? source,
        IFrameDriver frames,
        MotionSettings motion,
        GestureSettings? settings = null,
        IBrush? background = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(motion);
        _host = host;
        _root = root;
        _stack = stack;
        _factory = factory;
        _source = source;
        _frames = frames;
        _motion = motion;
        _settings = settings ?? GestureSettings.Default;
        _background = background ?? Brushes.Black;
        _stack.Changed += OnStackChanged;
        _motion.Changed += OnMotionChanged;
        Sync();
    }

    /// <summary>The predictive back host over the top two screens, or null at the root.</summary>
    public PredictiveBackHost? BackHost => _backHost;

    /// <summary>The view shown on top: the root view at the root, otherwise the pushed screen's view.</summary>
    public Control CurrentView => _views.Count == 0 ? _root : _views[^1].Wrapper;

    public void Dispose()
    {
        _stack.Changed -= OnStackChanged;
        _motion.Changed -= OnMotionChanged;
        DisposeBackHost();
    }

    private void OnStackChanged(object? sender, ScreenHistoryChangedEventArgs e) => Sync();

    private void OnMotionChanged(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            _controller.Level = _motion.Effective;
        }
    }

    private void Sync()
    {
        DisposeBackHost();

        // Keep the views of entries that are still on the stack in the same place; drop the rest and create the new ones.
        var entries = _stack.Entries;
        var keep = 0;
        while (keep < _views.Count && keep + 1 < entries.Count && _views[keep].Entry == entries[keep + 1])
        {
            keep++;
        }

        for (var i = _views.Count - 1; i >= keep; i--)
        {
            _host.Children.Remove(_views[i].Wrapper);
            _views.RemoveAt(i);
        }

        for (var i = keep + 1; i < entries.Count; i++)
        {
            var wrapper = Wrap(entries[i]);
            _host.Children.Add(wrapper);
            _views.Add((entries[i], wrapper));
        }

        // Only the top two screens are shown: the top, and the one a back swipe reveals. Only the top takes input.
        var depth = _stack.Depth;
        _root.IsVisible = depth <= 2;
        _root.IsHitTestVisible = depth == 1;
        for (var i = 0; i < _views.Count; i++)
        {
            var screenDepth = i + 2;
            _views[i].Wrapper.IsVisible = screenDepth >= depth;
            _views[i].Wrapper.IsHitTestVisible = screenDepth == depth;
        }

        MountBackHost();
    }

    private Border Wrap(ScreenEntry entry)
    {
        var body = _factory(entry)
            ?? new TextBlock { Text = entry.Key is null ? entry.Kind : $"{entry.Kind}: {entry.Key}", FontSize = 16, Margin = new Thickness(16) };
        return new Border { Background = _background, Child = body };
    }

    private void MountBackHost()
    {
        if (_source is null || !_stack.CanPop)
        {
            return;
        }

        var current = _views[^1].Wrapper;
        Control behind = _views.Count > 1 ? _views[^2].Wrapper : _root;
        var width = _host.Bounds.Width > 0 ? _host.Bounds.Width : _fallbackWidth;
        _controller = new PredictiveBackController(_source, width, _settings, _motion.Effective);
        _backHost = new PredictiveBackHost(current, behind, _controller, _frames);
        _backHost.PopRequested += OnPopRequested;
    }

    private void OnPopRequested(object? sender, EventArgs e) => _stack.Pop();

    private void DisposeBackHost()
    {
        if (_backHost is not null)
        {
            _backHost.PopRequested -= OnPopRequested;
            _backHost.Dispose();
            _backHost = null;
        }

        _controller?.Dispose();
        _controller = null;

        // A gesture cut short by a push or pop must not leave a screen scaled or shifted.
        _root.RenderTransform = null;
        foreach (var (_, wrapper) in _views)
        {
            wrapper.RenderTransform = null;
        }
    }
}
