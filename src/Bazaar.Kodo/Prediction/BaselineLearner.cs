namespace Bazaar.Kodo.Prediction;

/// <summary>The learned normal for one hour of the week. <see cref="WarmedUp"/> is false until enough weeks exist.</summary>
public readonly record struct BaselineReading(bool WarmedUp, double Median, double Scale, int Weeks);

/// <summary>
/// Per-install baseline: a robust location (median) and scale (MAD) per hour-of-week bucket over the trailing weeks
/// (doc 46 13.3). Memory is bounded by construction: at most 168 buckets with at most <c>TrailingWeeks + 1</c> entries each,
/// and one entry per bucket and week (the latest wins). Time is UTC. Nothing is shared between installs.
/// Not thread safe; the owner serialises access.
/// </summary>
public sealed class BaselineLearner
{
    private readonly BaselineOptions _options;
    private readonly List<Entry>[] _buckets = new List<Entry>[BaselineOptions.BucketCount];
    private long? _lastTicks;

    public BaselineLearner(BaselineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = [];
        }
    }

    /// <summary>Total stored values. Never above 168 x (TrailingWeeks + 1).</summary>
    public int StoredCount => _buckets.Sum(b => b.Count);

    /// <summary>The hour-of-week bucket and the week number of a time, or null before 1970.</summary>
    internal static (int Slot, long Week)? Locate(DateTimeOffset at)
    {
        long seconds = at.ToUnixTimeSeconds();
        if (seconds < 0)
        {
            return null;
        }

        long hour = seconds / 3600;
        return ((int)(hour % BaselineOptions.BucketCount), hour / BaselineOptions.BucketCount);
    }

    public SampleAcceptance Check(DateTimeOffset at, double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > _options.MaxAbsValue || Locate(at) is null)
        {
            return SampleAcceptance.Invalid;
        }

        return _lastTicks is { } last && at.UtcTicks <= last ? SampleAcceptance.OutOfOrder : SampleAcceptance.Accepted;
    }

    /// <summary>The baseline for the bucket of <paramref name="at"/>, from earlier weeks only.</summary>
    public BaselineReading Read(DateTimeOffset at)
    {
        if (Locate(at) is not { } where)
        {
            return default;
        }

        var values = new List<double>(_options.TrailingWeeks);
        foreach (Entry e in _buckets[where.Slot])
        {
            if (e.Week < where.Week && e.Week >= where.Week - _options.TrailingWeeks)
            {
                values.Add(e.Value);
            }
        }

        if (values.Count < _options.MinWeeks)
        {
            return new BaselineReading(false, 0, 0, values.Count);
        }

        double[] sorted = [.. values];
        double median = RobustStats.Median(sorted);
        for (int i = 0; i < sorted.Length; i++)
        {
            sorted[i] = Math.Abs(sorted[i] - median);
        }

        double mad = RobustStats.Median(sorted);
        double scale = Math.Max(
            _options.MadScale * mad,
            Math.Max(_options.RelativeScaleFloor * Math.Abs(median), _options.AbsoluteScaleFloor));
        return new BaselineReading(true, median, scale, values.Count);
    }

    public SampleAcceptance TryAdd(DateTimeOffset at, double value)
    {
        SampleAcceptance acceptance = Check(at, value);
        if (acceptance != SampleAcceptance.Accepted)
        {
            return acceptance;
        }

        (int slot, long week) = Locate(at)!.Value;
        List<Entry> bucket = _buckets[slot];
        bucket.RemoveAll(e => e.Week == week || e.Week < week - _options.TrailingWeeks);
        bucket.Add(new Entry(week, value));
        _lastTicks = at.UtcTicks;
        return SampleAcceptance.Accepted;
    }

    public BaselineMemory ToMemory()
    {
        var buckets = new List<BucketRecord>();
        for (int slot = 0; slot < _buckets.Length; slot++)
        {
            if (_buckets[slot].Count > 0)
            {
                buckets.Add(new BucketRecord(slot, [.. _buckets[slot].Select(e => new BucketEntryRecord(e.Week, e.Value))]));
            }
        }

        return new BaselineMemory(_lastTicks, buckets);
    }

    /// <summary>Rebuilds a learner from saved memory. Returns false when the memory is not valid, and the caller starts fresh.</summary>
    public static bool TryRestore(BaselineMemory? memory, BaselineOptions options, out BaselineLearner? learner)
    {
        learner = null;
        if (memory?.Buckets is null || memory.Buckets.Count > BaselineOptions.BucketCount)
        {
            return false;
        }

        if (memory.LastTicks is { } t && (t < DateTime.MinValue.Ticks || t > DateTime.MaxValue.Ticks))
        {
            return false;
        }

        var restored = new BaselineLearner(options) { _lastTicks = memory.LastTicks };
        var seen = new HashSet<int>();
        foreach (BucketRecord bucket in memory.Buckets)
        {
            if (bucket?.Entries is null
                || bucket.Slot is < 0 or >= BaselineOptions.BucketCount
                || !seen.Add(bucket.Slot)
                || bucket.Entries.Count > options.TrailingWeeks + 1)
            {
                return false;
            }

            var weeks = new HashSet<long>();
            foreach (BucketEntryRecord entry in bucket.Entries)
            {
                if (entry is null
                    || entry.Week < 0
                    || !double.IsFinite(entry.Value)
                    || Math.Abs(entry.Value) > options.MaxAbsValue
                    || !weeks.Add(entry.Week))
                {
                    return false;
                }

                restored._buckets[bucket.Slot].Add(new Entry(entry.Week, entry.Value));
            }
        }

        learner = restored;
        return true;
    }

    private readonly record struct Entry(long Week, double Value);
}
