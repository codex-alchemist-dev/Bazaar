namespace Bazaar.Client.Motion;

/// <summary>How far the client has stepped down because frames cost more than the budget allows (doc 93: no graphics settings, the client adapts).</summary>
public enum FrameBudgetDegradation
{
    /// <summary>Frames fit the budget.</summary>
    None,

    /// <summary>Frames run over: blur is dropped (the materials source's <c>FrameBudgetMissed</c>) and motion is capped at subtle.</summary>
    Light,

    /// <summary>Frames run far over: motion is capped at reduced as well.</summary>
    Heavy,
}

/// <summary>Tuning of <see cref="FrameBudgetMonitor"/>. The defaults suit a 60 Hz or faster display.</summary>
/// <param name="Budget">The frame time the client aims for.</param>
/// <param name="WindowSize">How many recent frame times the p95 is taken over.</param>
/// <param name="MinSamples">Frame times needed in the window before any verdict (a single dropped frame is not a trend).</param>
/// <param name="LightFactor">p95 above this multiple of the budget is <see cref="FrameBudgetDegradation.Light"/>.</param>
/// <param name="HeavyFactor">p95 above this multiple of the budget is <see cref="FrameBudgetDegradation.Heavy"/>.</param>
/// <param name="RecoverFactor">p95 must fall to this multiple of the budget or lower to step back up. Below <see cref="LightFactor"/>: that gap is the hysteresis.</param>
/// <param name="RecoverEvaluations">Consecutive good evaluations (one per MinSamples new frame times) before stepping back up one level.</param>
/// <param name="StaleAfter">A pause in animation this long forgets the old samples and steps back up one level, because a capped client animates too little to prove itself.</param>
public sealed record FrameBudgetOptions(
    TimeSpan Budget,
    int WindowSize = 90,
    int MinSamples = 45,
    double LightFactor = 1.5,
    double HeavyFactor = 3.0,
    double RecoverFactor = 1.15,
    int RecoverEvaluations = 3,
    TimeSpan StaleAfter = default)
{
    /// <summary>60 frames per second.</summary>
    public static FrameBudgetOptions Default { get; } = new(TimeSpan.FromSeconds(1.0 / 60), StaleAfter: TimeSpan.FromSeconds(45));

    /// <summary>The idle time after which old samples are forgotten.</summary>
    public TimeSpan EffectiveStaleAfter => StaleAfter > TimeSpan.Zero ? StaleAfter : TimeSpan.FromSeconds(45);
}

/// <summary>
/// Judges frame times over a rolling window and decides how far the client steps down (doc 93). Pure: it is fed frame times and
/// raises <see cref="Changed"/>; the shell turns the verdict into the materials flag and the motion cap. Stepping down is quick (the
/// first full window over the threshold); stepping up is slow (the p95 must sit under a lower threshold for several windows), so the
/// level does not flap around the limit. Not thread-safe: feed it from the UI thread, where frames arrive.
/// </summary>
public sealed class FrameBudgetMonitor
{
    private readonly FrameBudgetOptions _options;
    private readonly double[] _window;
    private int _count;
    private int _next;
    private int _sinceEvaluation;
    private int _goodEvaluations;

    public FrameBudgetMonitor(FrameBudgetOptions? options = null)
    {
        _options = options ?? FrameBudgetOptions.Default;
        if (_options.Budget <= TimeSpan.Zero || _options.WindowSize < 2 || _options.MinSamples < 1 || _options.MinSamples > _options.WindowSize)
        {
            throw new ArgumentException("Frame budget options are out of range.", nameof(options));
        }

        _window = new double[_options.WindowSize];
    }

    /// <summary>The current verdict.</summary>
    public FrameBudgetDegradation Degradation { get; private set; }

    /// <summary>Raised when <see cref="Degradation"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The 95th percentile frame time over the window, or null before <see cref="FrameBudgetOptions.MinSamples"/> frames.</summary>
    public TimeSpan? P95 => _count < _options.MinSamples ? null : TimeSpan.FromMilliseconds(Percentile95());

    /// <summary>Records the time one frame took (the gap between two consecutive frames of a running animation).</summary>
    public void Record(TimeSpan frameTime)
    {
        if (frameTime <= TimeSpan.Zero)
        {
            return;
        }

        _window[_next] = frameTime.TotalMilliseconds;
        _next = (_next + 1) % _window.Length;
        _count = Math.Min(_count + 1, _window.Length);
        _sinceEvaluation++;

        if (_count >= _options.MinSamples && _sinceEvaluation >= _options.MinSamples)
        {
            _sinceEvaluation = 0;
            Evaluate();
        }
    }

    /// <summary>Tells the monitor that animation resumed after <paramref name="idle"/> of stillness.</summary>
    public void NoteIdle(TimeSpan idle)
    {
        if (idle < _options.EffectiveStaleAfter)
        {
            return;
        }

        _count = 0;
        _next = 0;
        _sinceEvaluation = 0;
        _goodEvaluations = 0;
        Set(Degradation == FrameBudgetDegradation.None ? FrameBudgetDegradation.None : Degradation - 1);
    }

    private void Evaluate()
    {
        double budget = _options.Budget.TotalMilliseconds;
        double p95 = Percentile95();
        FrameBudgetDegradation over = p95 > budget * _options.HeavyFactor ? FrameBudgetDegradation.Heavy
            : p95 > budget * _options.LightFactor ? FrameBudgetDegradation.Light
            : FrameBudgetDegradation.None;

        if (over > Degradation)
        {
            _goodEvaluations = 0;
            Set(over);
        }
        else if (Degradation != FrameBudgetDegradation.None && p95 <= budget * _options.RecoverFactor)
        {
            if (++_goodEvaluations >= _options.RecoverEvaluations)
            {
                _goodEvaluations = 0;
                Set(Degradation - 1);
            }
        }
        else
        {
            // Between the recover and degrade thresholds: hold the level and restart the recovery count.
            _goodEvaluations = 0;
        }
    }

    private double Percentile95()
    {
        Span<double> sorted = stackalloc double[_count];
        for (int i = 0; i < _count; i++)
        {
            sorted[i] = _window[i];
        }

        sorted.Sort();
        // Nearest rank: the smallest value at or above 95% of the samples.
        int rank = (int)Math.Ceiling(_count * 0.95);
        return sorted[Math.Clamp(rank - 1, 0, _count - 1)];
    }

    private void Set(FrameBudgetDegradation value)
    {
        if (value == Degradation)
        {
            return;
        }

        Degradation = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
