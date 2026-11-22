using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Prediction;

/// <summary>
/// A probe-shaped wrapper around one series' baseline and drift. <c>Observe</c> takes a value (time from the supplied
/// <see cref="TimeProvider"/>) and returns the result: unknown while warming up or after a bad sample, green when
/// stable, amber with a cause on drift. Advisory only. Not thread safe.
/// </summary>
public sealed class BaselineDriftProbe
{
    private readonly TimeProvider _time;
    private readonly BaselineOptions _options;
    private BaselineDriftMonitor _monitor;

    public BaselineDriftProbe(string id, TimeProvider time, BaselineOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(time);

        Id = id;
        _time = time;
        _options = options ?? new BaselineOptions();
        _monitor = new BaselineDriftMonitor(_options);
    }

    public string Id { get; }

    public DriftAssessment? LastAssessment { get; private set; }

    public ProbeResult Observe(double value) => Observe(_time.GetUtcNow(), value);

    public ProbeResult Observe(DateTimeOffset at, double value)
    {
        DriftAssessment assessment = _monitor.Observe(at, value);
        LastAssessment = assessment;
        return PredictionProbeMapper.ToProbeResult(assessment);
    }

    /// <summary>Forgets everything learned. The owner action behind a baseline reset (doc 46 13.3); the caller audits it.</summary>
    public void Reset()
    {
        _monitor = new BaselineDriftMonitor(_options);
        LastAssessment = null;
    }

    public PredictionMemory ToMemory() =>
        new(PredictionMemory.CurrentVersion, null, _monitor.ToBaselineMemory(), _monitor.ToDriftMemory());

    /// <summary>Restores saved state. Returns false, and keeps the current state, when the memory is not valid.</summary>
    public bool TryRestore(PredictionMemory? memory)
    {
        if (memory is null
            || memory.Version != PredictionMemory.CurrentVersion
            || !BaselineDriftMonitor.TryRestore(memory.Baseline, memory.Drift, _options, out BaselineDriftMonitor? restored))
        {
            return false;
        }

        _monitor = restored!;
        return true;
    }
}
