using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Prediction;

/// <summary>
/// A probe-shaped wrapper around one series' trend forecast. Feed it values with <c>Record</c>, ask for the
/// <see cref="ProbeResult"/> with <c>Evaluate</c>. A rejected sample leaves the probe unknown with its cause until the
/// next accepted sample. Time comes from the supplied <see cref="TimeProvider"/>. Advisory only. Not thread safe.
/// </summary>
public sealed class TrendForecastProbe
{
    private readonly TimeProvider _time;
    private readonly TrendOptions _options;
    private readonly double _threshold;
    private readonly ThresholdDirection _direction;
    private SampleWindow _window;
    private string? _rejection;

    public TrendForecastProbe(
        string id, TimeProvider time, double threshold, ThresholdDirection direction, TrendOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(time);

        Id = id;
        _time = time;
        _threshold = threshold;
        _direction = direction;
        _options = options ?? new TrendOptions();
        _window = new SampleWindow(_options);
    }

    public string Id { get; }

    public TrendForecast? LastForecast { get; private set; }

    public SampleAcceptance Record(double value) => Record(_time.GetUtcNow(), value);

    public SampleAcceptance Record(DateTimeOffset at, double value)
    {
        SampleAcceptance acceptance = _window.TryAdd(at, value);
        _rejection = acceptance.ToCause();
        return acceptance;
    }

    public ProbeResult Evaluate()
    {
        if (_rejection is not null)
        {
            return ProbeResult.Unknown(_rejection);
        }

        TrendForecast forecast = TrendForecaster.Forecast(
            _window.Samples, _threshold, _direction, _options, _time.GetUtcNow());
        LastForecast = forecast;
        return PredictionProbeMapper.ToProbeResult(forecast, _options);
    }

    public PredictionMemory ToMemory() =>
        new(PredictionMemory.CurrentVersion, _window.ToMemory(), null, null);

    /// <summary>Restores saved samples. Returns false, and keeps the current state, when the memory is not valid.</summary>
    public bool TryRestore(PredictionMemory? memory)
    {
        if (memory is null
            || memory.Version != PredictionMemory.CurrentVersion
            || !SampleWindow.TryRestore(memory.Trend, _options, out SampleWindow? restored))
        {
            return false;
        }

        _window = restored!;
        _rejection = null;
        return true;
    }
}
