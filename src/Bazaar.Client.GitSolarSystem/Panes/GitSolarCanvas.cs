using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>
/// The drawn solar system (the Skia fallback path). Each branch is an orbit ring about the default-branch core, each commit a
/// body coloured by its branch's palette entry, parent edges are thin lines and merged-branch comets are thicker dashed lines
/// with an arrowhead at the merge commit. Positions come from the latest frame, eased by <see cref="FrameInterpolator"/>; the
/// timer runs only while a frame is easing. Drag pans, the wheel zooms, and a click selects the nearest commit within 8 px.
/// The model stays out of this control: the pane view pushes the scene, frame and selection in through <see cref="Load"/>.
/// </summary>
public sealed class GitSolarCanvas : Control
{
    private const double _hitRadiusPixels = 8;
    private const double _dragThresholdPixels = 4;
    private const double _animationSeconds = 0.25;
    private const float _fitPaddingPixels = 24f;
    private const double _arrowLengthPixels = 10;
    private const double _arrowHalfWidthPixels = 4;

    private static readonly IBrush _ringBrush = new SolidColorBrush(Color.FromArgb(70, 160, 170, 190));
    private static readonly Pen _ringPen = new(_ringBrush, 1);
    private static readonly IBrush _edgeBrush = new SolidColorBrush(Color.FromArgb(120, 150, 150, 150));
    private static readonly Pen _edgePen = new(_edgeBrush, 1);
    private static readonly IBrush _cometBrush = new SolidColorBrush(Color.FromArgb(220, 240, 180, 60));
    private static readonly Pen _cometPen = new(_cometBrush, 2.5, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IBrush _selectionBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private static readonly Pen _selectionPen = new(_selectionBrush, 2);
    private static readonly IBrush _staleBrush = new SolidColorBrush(Color.FromArgb(90, 120, 120, 120));
    private static readonly IBrush[] _clusterBrushes = BuildClusterBrushes();

    private readonly FrameInterpolator _interpolator = new(_animationSeconds);
    private readonly Camera2D _camera = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private GitSolarScene? _scene;
    private int[] _branchOfNode = [];
    private RenderFrame? _frame;
    private string? _selectedSha;
    private bool _stale;
    private bool _needsFit;
    private float[] _sampled = [];
    private double _animateUntil;
    private Point? _pressAt;
    private Point _lastPointer;
    private bool _dragging;

    public GitSolarCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTimerTick);
    }

    /// <summary>The number of commits drawn: the node count of the frame in use, or 0 when no frame applies.</summary>
    public int NodeCount => _frame?.NodeCount ?? 0;

    /// <summary>True while the canvas shows the stale tint.</summary>
    public bool IsStale => _stale;

    /// <summary>The pan and zoom of the view.</summary>
    public Camera2D Camera => _camera;

    /// <summary>Raised with the sha of the commit a click selected.</summary>
    public event Action<string>? NodeClicked;

    /// <summary>
    /// Brings the canvas in line with the model. A new scene refits the view on the next render. A new frame starts an
    /// ease from the positions drawn now; the same frame object is not pushed twice.
    /// </summary>
    public void Load(GitSolarScene? scene, RenderFrame? frame, string? selectedSha, bool stale)
    {
        if (!ReferenceEquals(scene, _scene))
        {
            _scene = scene;
            _branchOfNode = scene is null ? [] : scene.BranchOfNode.ToArray();
            _needsFit = scene is not null;
        }

        _selectedSha = selectedSha;
        _stale = stale;

        if (frame is null)
        {
            _frame = null;
            _interpolator.Reset();
        }
        else if (!ReferenceEquals(frame, _frame))
        {
            _frame = frame;
            double now = Now();
            _interpolator.Push(frame, now);
            _animateUntil = now + _animationSeconds;
            StartTimerIfAnimating();
        }

        InvalidateVisual();
    }

    /// <summary>The screen point of a commit at the current sample, or null when the commit is not drawn.</summary>
    public Point? NodeScreenPosition(string sha)
    {
        if (_scene is null || !SamplePositions())
        {
            return null;
        }

        int index = IndexOfSha(sha);
        if (index < 0)
        {
            return null;
        }

        var (x, y) = _camera.WorldToScreen(_sampled[2 * index], _sampled[(2 * index) + 1], ViewWidth(), ViewHeight());
        return new Point(x, y);
    }

    /// <summary>The sha of the commit nearest to a screen point within 8 px, or null when none is that close.</summary>
    public string? HitTest(Point screenPoint)
    {
        if (_scene is null || !SamplePositions())
        {
            return null;
        }

        var (wx, wy) = _camera.ScreenToWorld((float)screenPoint.X, (float)screenPoint.Y, ViewWidth(), ViewHeight());
        float radius = (float)(_hitRadiusPixels / _camera.Zoom);
        int hit = GitSolarGeometry.NearestNode(_sampled, wx, wy, radius);
        return hit >= 0 && hit < _scene.Shas.Count ? _scene.Shas[hit] : null;
    }

    /// <summary>A click at a screen point: selects the nearest commit through <see cref="NodeClicked"/>. Used by the pointer release.</summary>
    public void ClickAt(Point screenPoint)
    {
        string? sha = HitTest(screenPoint);
        if (sha is not null)
        {
            NodeClicked?.Invoke(sha);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_scene is null || _frame is null)
        {
            return;
        }

        int width = ViewWidth();
        int height = ViewHeight();
        if (_needsFit && Bounds.Width >= 1 && Bounds.Height >= 1)
        {
            GitSolarViewFit fit = GitSolarGeometry.FitView(_frame.Positions.Span, width, height, _fitPaddingPixels);
            GitSolarGeometry.ApplyFit(_camera, fit, width, height);
            _needsFit = false;
        }

        if (!SamplePositions())
        {
            return;
        }

        int count = _frame.NodeCount;
        var screen = new Point[count];
        for (int i = 0; i < count; i++)
        {
            var (sx, sy) = _camera.WorldToScreen(_sampled[2 * i], _sampled[(2 * i) + 1], width, height);
            screen[i] = new Point(sx, sy);
        }

        DrawOrbits(context, width, height);
        DrawEdges(context, screen);
        DrawNodes(context, screen);

        if (_stale)
        {
            context.FillRectangle(_staleBrush, new Rect(0, 0, width, height));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartTimerIfAnimating();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        _pressAt = point;
        _lastPointer = point;
        _dragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressAt is not { } press)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!_dragging && Distance(press, point) >= _dragThresholdPixels)
        {
            _dragging = true;
        }

        if (_dragging)
        {
            _camera.PanPixels((float)(point.X - _lastPointer.X), (float)(point.Y - _lastPointer.Y));
            _lastPointer = point;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        bool click = _pressAt is not null && !_dragging;
        var release = e.GetPosition(this);
        _pressAt = null;
        _dragging = false;
        if (click)
        {
            ClickAt(release);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        _camera.ZoomAt((float)point.X, (float)point.Y, ViewWidth(), ViewHeight(), e.Delta.Y > 0 ? 1.1f : 1f / 1.1f);
        InvalidateVisual();
    }

    private static IBrush[] BuildClusterBrushes()
    {
        var brushes = new IBrush[ConstellationNodeStyles.Palette.Count];
        for (int i = 0; i < brushes.Length; i++)
        {
            var style = ConstellationNodeStyles.Palette[i];
            brushes[i] = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(style.Alpha * 255f),
                (byte)Math.Round(style.Red * 255f),
                (byte)Math.Round(style.Green * 255f),
                (byte)Math.Round(style.Blue * 255f)));
        }

        return brushes;
    }

    private void DrawOrbits(DrawingContext context, int width, int height)
    {
        if (_scene is null || _branchOfNode.Length == 0)
        {
            return;
        }

        var xy = _sampled.AsSpan();
        var branches = _branchOfNode.AsSpan();
        var (cx, cy) = GitSolarGeometry.CoreCentre(xy, branches);
        var centre = _camera.WorldToScreen(cx, cy, width, height);
        for (int branch = 0; branch < _scene.BranchNames.Count; branch++)
        {
            float radius = GitSolarGeometry.RingRadius(xy, branches, branch, cx, cy);
            double screenRadius = radius * _camera.Zoom;
            if (screenRadius < 1)
            {
                continue;
            }

            context.DrawEllipse(null, _ringPen, new Point(centre.X, centre.Y), screenRadius, screenRadius);
        }
    }

    private void DrawEdges(DrawingContext context, Point[] screen)
    {
        var pairs = _scene!.Scene.EdgePairs.Span;
        int edgeCount = pairs.Length / 2;
        for (int edge = 0; edge < edgeCount; edge++)
        {
            int from = pairs[2 * edge];
            int to = pairs[(2 * edge) + 1];
            if ((uint)from >= (uint)screen.Length || (uint)to >= (uint)screen.Length)
            {
                continue;
            }

            Point start = screen[from];
            Point end = screen[to];
            if (!_scene.CometEdges.Contains(edge))
            {
                context.DrawLine(_edgePen, start, end);
                continue;
            }

            context.DrawLine(_cometPen, start, end);
            DrawArrowhead(context, start, end);
        }
    }

    /// <summary>The comet arrow points at the merge commit (the edge's target), stopped short of the node body.</summary>
    private static void DrawArrowhead(DrawingContext context, Point start, Point end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < _arrowLengthPixels * 2)
        {
            return;
        }

        double ux = dx / length;
        double uy = dy / length;
        double stop = 6;
        var tip = new Point(end.X - (ux * stop), end.Y - (uy * stop));
        var back = new Point(tip.X - (ux * _arrowLengthPixels), tip.Y - (uy * _arrowLengthPixels));
        var left = new Point(back.X - (uy * _arrowHalfWidthPixels), back.Y + (ux * _arrowHalfWidthPixels));
        var right = new Point(back.X + (uy * _arrowHalfWidthPixels), back.Y - (ux * _arrowHalfWidthPixels));

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(tip, true);
            sink.LineTo(left);
            sink.LineTo(right);
            sink.EndFigure(true);
        }

        context.DrawGeometry(_cometBrush, null, geometry);
    }

    private void DrawNodes(DrawingContext context, Point[] screen)
    {
        int selected = IndexOfSha(_selectedSha);
        for (int i = 0; i < screen.Length; i++)
        {
            int cluster = _branchOfNode.Length > i ? _branchOfNode[i] : 0;
            int palette = ConstellationNodeStyles.Palette.Count;
            int slot = ((cluster % palette) + palette) % palette;
            double radius = ConstellationNodeStyles.Palette[slot].Radius;
            context.DrawEllipse(_clusterBrushes[slot], null, screen[i], radius, radius);
            if (i == selected)
            {
                context.DrawEllipse(null, _selectionPen, screen[i], radius + 3, radius + 3);
            }
        }
    }

    /// <summary>Samples the eased positions into the reusable buffer. Returns false when no frame applies.</summary>
    private bool SamplePositions()
    {
        if (_frame is null || !_interpolator.HasFrame)
        {
            return false;
        }

        int length = _frame.NodeCount * 2;
        if (_sampled.Length != length)
        {
            _sampled = new float[length];
        }

        return _interpolator.Sample(Now(), _sampled);
    }

    private int IndexOfSha(string? sha)
    {
        if (_scene is null || sha is null)
        {
            return -1;
        }

        for (int i = 0; i < _scene.Shas.Count; i++)
        {
            if (string.Equals(_scene.Shas[i], sha, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void StartTimerIfAnimating()
    {
        if (Now() < _animateUntil && !_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if (Now() >= _animateUntil)
        {
            _timer.Stop();
        }
    }

    private double Now() => _clock.Elapsed.TotalSeconds;

    private int ViewWidth() => (int)Math.Max(1, Bounds.Width);

    private int ViewHeight() => (int)Math.Max(1, Bounds.Height);

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
