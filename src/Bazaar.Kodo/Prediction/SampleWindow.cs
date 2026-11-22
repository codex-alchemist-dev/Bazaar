namespace Bazaar.Kodo.Prediction;

/// <summary>
/// A bounded, ordered window of samples for one series. Memory never exceeds <see cref="TrendOptions.MaxSamples"/>:
/// samples older than the window and the oldest samples past the cap are dropped. Rejected samples change nothing.
/// Not thread safe; the owner serialises access.
/// </summary>
public sealed class SampleWindow
{
    private readonly TrendOptions _options;
    private readonly List<TrendSample> _samples = [];

    public SampleWindow(TrendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public int Count => _samples.Count;

    public IReadOnlyList<TrendSample> Samples => _samples;

    /// <summary>Checks a sample against the window without changing it.</summary>
    public SampleAcceptance Check(DateTimeOffset at, double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > _options.MaxAbsValue)
        {
            return SampleAcceptance.Invalid;
        }

        return _samples.Count > 0 && at <= _samples[^1].At ? SampleAcceptance.OutOfOrder : SampleAcceptance.Accepted;
    }

    public SampleAcceptance TryAdd(DateTimeOffset at, double value)
    {
        SampleAcceptance acceptance = Check(at, value);
        if (acceptance != SampleAcceptance.Accepted)
        {
            return acceptance;
        }

        _samples.Add(new TrendSample(at, value));
        DateTimeOffset cutoff = RobustStats.Before(at, _options.Window);
        int drop = 0;
        while (drop < _samples.Count - 1 && _samples[drop].At < cutoff)
        {
            drop++;
        }

        drop = Math.Max(drop, _samples.Count - _options.MaxSamples);
        if (drop > 0)
        {
            _samples.RemoveRange(0, drop);
        }

        return SampleAcceptance.Accepted;
    }

    public TrendMemory ToMemory() =>
        new([.. _samples.Select(s => new SampleRecord(s.At.UtcTicks, s.Value))]);

    /// <summary>Rebuilds a window from saved memory. Returns false when the memory is not valid, and the caller starts fresh.</summary>
    public static bool TryRestore(TrendMemory? memory, TrendOptions options, out SampleWindow? window)
    {
        window = null;
        if (memory?.Samples is null || memory.Samples.Count > options.MaxSamples)
        {
            return false;
        }

        var restored = new SampleWindow(options);
        foreach (SampleRecord record in memory.Samples)
        {
            if (record is null || record.At < DateTime.MinValue.Ticks || record.At > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            var at = new DateTimeOffset(record.At, TimeSpan.Zero);
            if (restored.TryAdd(at, record.Value) != SampleAcceptance.Accepted)
            {
                return false;
            }
        }

        window = restored;
        return true;
    }
}
