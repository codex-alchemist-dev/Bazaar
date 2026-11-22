using Bazaar.Client.Motion;

namespace Bazaar.Client.Gestures;

/// <summary>How the two screens are drawn at one back-gesture progress (doc 93, "Predictive back").</summary>
/// <param name="Scale">Scale of the current screen.</param>
/// <param name="TranslateX">Shift of the current screen in dp; positive is to the right.</param>
/// <param name="BehindScale">Scale of the screen beneath, growing to 1 as the swipe commits.</param>
public readonly record struct BackTransform(double Scale, double TranslateX, double BehindScale)
{
    /// <summary>Nothing moves (rest, and the reduced motion level).</summary>
    public static BackTransform Identity { get; } = new(1, 0, 1);
}

/// <summary>Maps back progress to the transform of the current screen and the one beneath. The same mapping drives an in-app swipe.</summary>
public static class PredictiveBackMapper
{
    /// <summary>The current screen scales down and shifts away from the swipe edge; the one beneath grows toward full size. Progress is clamped to 0..1.</summary>
    public static BackTransform Map(double progress, BackGestureEdge edge, double screenWidth, GestureSettings settings)
    {
        var p = Math.Clamp(progress, 0.0, 1.0);
        var direction = edge == BackGestureEdge.Left ? 1.0 : -1.0;
        return new BackTransform(
            Scale: 1 - (1 - settings.BackMinScale) * p,
            TranslateX: direction * settings.BackMaxShift * screenWidth * p,
            BehindScale: settings.BackBehindMinScale + (1 - settings.BackBehindMinScale) * p);
    }
}

/// <summary>
/// Drives the predictive back transition from an <see cref="IBackGestureSource"/>: progress follows the system gesture, and release commits
/// or cancels with the Fluid spring from the current progress. When the source is not predictive (below Android 14, or the head cannot
/// forward the callback) a commit pops at once, the standard instant pop; the same at the reduced motion level, where nothing is scaled.
/// </summary>
public sealed class PredictiveBackController : IDisposable
{
    private readonly IBackGestureSource _source;
    private readonly SpringValue _progress = new(0.002);
    private readonly double _screenWidth;
    private GestureSettings _settings;
    private BackGestureEdge _edge;
    private bool _committing;

    public PredictiveBackController(IBackGestureSource source, double screenWidth, GestureSettings? settings = null, MotionLevel level = MotionLevel.Liquid)
    {
        _source = source;
        _screenWidth = screenWidth;
        _settings = (settings ?? GestureSettings.Default).Clamped();
        Level = level;
        source.Started += OnStarted;
        source.Progressed += OnProgressed;
        source.Committed += OnCommitted;
        source.Cancelled += OnCancelled;
    }

    public MotionLevel Level { get; set; }

    /// <summary>True from the start of a gesture until its spring has settled.</summary>
    public bool IsActive { get; private set; }

    public double Progress => _progress.Position;

    /// <summary>The transform to draw now; the identity at rest and at the reduced level.</summary>
    public BackTransform Transform =>
        !IsActive || Level == MotionLevel.Reduced ? BackTransform.Identity : PredictiveBackMapper.Map(_progress.Position, _edge, _screenWidth, _settings);

    /// <summary>Raised whenever <see cref="Transform"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once when the screen should be popped (after the commit spring has settled, or at once without live progress).</summary>
    public event EventHandler? PopRequested;

    public void Configure(GestureSettings settings) => _settings = settings.Clamped();

    /// <summary>Moves the commit or cancel spring on. Returns true while it is still running.</summary>
    public bool Advance(TimeSpan delta)
    {
        if (!IsActive || !_progress.IsAnimating)
        {
            return false;
        }

        var running = _progress.Advance(delta);
        Changed?.Invoke(this, EventArgs.Empty);
        if (running)
        {
            return true;
        }

        var commit = _committing;
        IsActive = false;
        _committing = false;
        _progress.Set(0);
        Changed?.Invoke(this, EventArgs.Empty);
        if (commit)
        {
            PopRequested?.Invoke(this, EventArgs.Empty);
        }

        return false;
    }

    public void Dispose()
    {
        _source.Started -= OnStarted;
        _source.Progressed -= OnProgressed;
        _source.Committed -= OnCommitted;
        _source.Cancelled -= OnCancelled;
    }

    private void OnStarted(object? sender, BackGestureEventArgs e)
    {
        _edge = e.Edge;
        _committing = false;
        IsActive = true;
        _progress.Set(e.Progress);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnProgressed(object? sender, BackGestureEventArgs e)
    {
        if (!IsActive)
        {
            OnStarted(sender, e);
            return;
        }

        _edge = e.Edge;
        _progress.Set(e.Progress);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCommitted(object? sender, BackGestureEventArgs e)
    {
        if (!_source.IsPredictive || !IsActive || Level == MotionLevel.Reduced)
        {
            IsActive = false;
            _committing = false;
            _progress.Set(0);
            Changed?.Invoke(this, EventArgs.Empty);
            PopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _edge = e.Edge;
        _committing = true;
        _progress.AnimateTo(1, _settings.SettleDamping, _settings.SettleTime);
    }

    private void OnCancelled(object? sender, BackGestureEventArgs e)
    {
        if (!IsActive)
        {
            return;
        }

        _committing = false;
        if (Level == MotionLevel.Reduced)
        {
            IsActive = false;
            _progress.Set(0);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _progress.AnimateTo(0, _settings.SettleDamping, _settings.SettleTime);
    }
}
