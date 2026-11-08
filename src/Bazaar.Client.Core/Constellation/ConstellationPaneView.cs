using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Constellation.Rendering;

namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// Avalonia view of the Constellation 2D pane: the drawn graph (the Skia fallback path) beside the accessible
/// node list. Pan by dragging, zoom with the wheel, focus by clicking a node or pressing a keymap key.
/// Positions are interpolated between server frames. The Vulkan backend (<c>IConstellationRenderer</c>) is not
/// attached here yet, because its swapchain is not implemented.
/// </summary>
public sealed class ConstellationPaneView : Grid
{
    private const double _hitRadiusPixels = 8;
    private const double _dragThresholdPixels = 4;
    private static readonly IBrush _edgeBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
    private static readonly IBrush _nodeBrush = new SolidColorBrush(Color.FromRgb(120, 170, 220));
    private static readonly IBrush _focusBrush = new SolidColorBrush(Color.FromRgb(240, 180, 60));
    private static readonly Pen _edgePen = new(_edgeBrush, 1);

    private readonly ConstellationPaneViewModel _pane;
    private readonly ListBox _list;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConstellationCanvas _canvas;
    private readonly TextBlock _placeholder;
    private Point? _pressAt;
    private Point _lastPointer;
    private bool _dragging;
    private float[] _sampled = [];

    public ConstellationPaneView(ConstellationPaneViewModel pane)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        ColumnDefinitions = new ColumnDefinitions("*,220");
        MinHeight = 320;
        Focusable = true;

        _canvas = new ConstellationCanvas(this);
        _placeholder = new TextBlock
        {
            Text = "No graph data yet. The view fills in once the server sends a graph snapshot.",
            Margin = new Thickness(16),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.7,
        };
        _list = new ListBox
        {
            Margin = new Thickness(8, 0, 0, 0),
            ItemsSource = AccessibleRows(),
        };
        _list.SelectionChanged += OnListSelectionChanged;
        Grid.SetColumn(_list, 1);
        Children.Add(_canvas);
        Children.Add(_placeholder);
        Children.Add(_list);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _pane.PropertyChanged += (_, _) => Refresh();
        SizeChanged += (_, _) => RequestVisibleTiles();
        _placeholder.IsVisible = !_pane.HasData;
    }

    public ConstellationPaneViewModel Pane => _pane;

    /// <summary>True while the pane shows its no-data placeholder instead of a graph.</summary>
    public bool ShowsPlaceholder => _placeholder.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Pushes a server frame into the pane and schedules a redraw.</summary>
    public bool ApplyFrame(RenderFrame frame)
    {
        bool applied = _pane.ApplyFrame(frame, Now());
        if (applied)
        {
            _canvas.InvalidateVisual();
        }

        return applied;
    }

    private double Now() => _clock.Elapsed.TotalSeconds;

    private void Tick()
    {
        if (_pane.IsAnimating(Now()))
        {
            _canvas.InvalidateVisual();
        }
    }

    private void Refresh()
    {
        _list.ItemsSource = AccessibleRows();
        _placeholder.IsVisible = !_pane.HasData;
        _canvas.InvalidateVisual();
    }

    /// <summary>Asks the pane for the nodes in the box the camera currently shows. Only cancellation is swallowed.</summary>
    private async void RequestVisibleTiles()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        try
        {
            await _pane.SetViewportAsync(_pane.VisibleBox((int)Bounds.Width, (int)Bounds.Height));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private List<string> AccessibleRows() => _pane.AccessibleList.Items
        .Select(item => $"{(item.IsFocused ? "▶ " : string.Empty)}{item.Label} ({item.Kind})")
        .ToList();

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _pane.AccessibleList.Items.Count)
        {
            return;
        }

        _pane.Command(new ConstellationCommand(ConstellationCommandKind.Focus, _pane.AccessibleList.Items[index].NodeId));
    }

    /// <summary>Draws the graph. Kept private to the view; the model stays free of Avalonia types.</summary>
    private sealed class ConstellationCanvas : Control
    {
        private readonly ConstellationPaneView _owner;

        public ConstellationCanvas(ConstellationPaneView owner) => _owner = owner;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var pane = _owner._pane;
            int count = pane.Nodes.Count;
            int width = (int)Math.Max(1, Bounds.Width);
            int height = (int)Math.Max(1, Bounds.Height);

            // Tile nodes come from the viewport request and are drawn as they are; they skip the layout interpolator.
            if (pane.TileNodes.Count > 0)
            {
                foreach (var node in pane.TileNodes)
                {
                    var (tx, ty) = pane.Camera.WorldToScreen(node.X, node.Y, width, height);
                    context.DrawEllipse(_nodeBrush, null, new Point(tx, ty), 2.5, 2.5);
                }

                return;
            }

            if (count == 0)
            {
                return;
            }

            if (_owner._sampled.Length != count * 2)
            {
                _owner._sampled = new float[count * 2];
            }

            // Sample from interpolator; without a frame, nodes sit at the origin and draw nothing useful.
            if (!pane.Interpolator.Sample(_owner.Now(), _owner._sampled))
            {
                return;
            }

            float[] xy = _owner._sampled;
            var screen = new Point[count];
            for (int i = 0; i < count; i++)
            {
                var (sx, sy) = pane.Camera.WorldToScreen(xy[i * 2], xy[(i * 2) + 1], width, height);
                screen[i] = new Point(sx, sy);
            }

            var edges = pane.Scene.EdgePairs.Span;
            for (int e = 0; e + 1 < edges.Length; e += 2)
            {
                context.DrawLine(_edgePen, screen[edges[e]], screen[edges[e + 1]]);
            }

            for (int i = 0; i < count; i++)
            {
                bool focused = pane.IsFocused(pane.Nodes[i].Id);
                double radius = focused ? 7 : 4;
                context.DrawEllipse(focused ? _focusBrush : _nodeBrush, null, screen[i], radius, radius);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetPosition(this);
            _owner._pressAt = point;
            _owner._lastPointer = point;
            _owner._dragging = false;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_owner._pressAt is not { } press)
            {
                return;
            }

            var point = e.GetPosition(this);
            if (!_owner._dragging && Distance(press, point) >= _dragThresholdPixels)
            {
                _owner._dragging = true;
            }

            if (_owner._dragging)
            {
                _owner._pane.Camera.PanPixels((float)(point.X - _owner._lastPointer.X), (float)(point.Y - _owner._lastPointer.Y));
                _owner._lastPointer = point;
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
            bool click = _owner._pressAt is not null && !_owner._dragging;
            bool dragged = _owner._dragging;
            var release = e.GetPosition(this);
            _owner._pressAt = null;
            if (click)
            {
                FocusAt(release);
            }

            if (dragged)
            {
                _owner.RequestVisibleTiles();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var point = e.GetPosition(this);
            _owner._pane.Camera.ZoomAt((float)point.X, (float)point.Y, (int)Bounds.Width, (int)Bounds.Height,
                e.Delta.Y > 0 ? 1.1f : 1f / 1.1f);
            InvalidateVisual();
            _owner.RequestVisibleTiles();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            string? symbol = e.KeySymbol;
            if (symbol is { Length: 1 } && ConstellationKeymap.TryResolve(symbol, out var kind))
            {
                _owner._pane.Command(new ConstellationCommand(kind));
                e.Handled = true;
            }
        }

        private void FocusAt(Point screenPoint)
        {
            var pane = _owner._pane;
            int width = (int)Bounds.Width;
            int height = (int)Bounds.Height;
            var (wx, wy) = pane.Camera.ScreenToWorld((float)screenPoint.X, (float)screenPoint.Y, width, height);
            float radiusWorld = (float)(_hitRadiusPixels / pane.Camera.Zoom);
            int hit = pane.Grid.NearestNode(wx, wy, radiusWorld);
            if (hit >= 0 && hit < pane.Nodes.Count)
            {
                pane.Command(new ConstellationCommand(ConstellationCommandKind.Focus, pane.Nodes[hit].Id));
            }
        }

        private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
    }
}
