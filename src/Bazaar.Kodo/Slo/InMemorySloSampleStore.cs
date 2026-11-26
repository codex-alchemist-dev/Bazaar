using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Slo;

/// <summary>
/// An in-memory <see cref="ISloSampleStore"/> for tests and for a host that does not need to survive a restart. Bounded in series
/// (default 256) and in slots per series (default 44640, 31 days at one minute). Safe for concurrent callers.
/// </summary>
public sealed class InMemorySloSampleStore : ISloSampleStore
{
    private const int _defaultMaxSlots = 44_640;
    private const int _defaultMaxSeries = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, SortedDictionary<long, SlotCounts>> _series = new(StringComparer.Ordinal);
    private readonly int _maxSeries;

    /// <summary>Creates a store. The resolution is from 1 second to 1 hour.</summary>
    public InMemorySloSampleStore(TimeSpan? resolution = null, int maxSlots = _defaultMaxSlots, int maxSeries = _defaultMaxSeries)
    {
        TimeSpan value = resolution ?? TimeSpan.FromMinutes(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromHours(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSlots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSeries, 1);
        Resolution = value;
        MaxSlots = maxSlots;
        _maxSeries = maxSeries;
    }

    /// <inheritdoc />
    public TimeSpan Resolution { get; }

    /// <inheritdoc />
    public int MaxSlots { get; }

    /// <inheritdoc />
    public bool Record(string sloId, DateTimeOffset at, SloSampleOutcome outcome)
    {
        if (string.IsNullOrEmpty(sloId) || !KodoExportPatterns.IdentifierName().IsMatch(sloId) || !Enum.IsDefined(outcome))
        {
            return false;
        }

        long slot = SloSlots.IndexOf(at, Resolution);
        lock (_gate)
        {
            if (!_series.TryGetValue(sloId, out SortedDictionary<long, SlotCounts>? slots))
            {
                if (_series.Count >= _maxSeries)
                {
                    return false;
                }

                slots = [];
                _series[sloId] = slots;
            }

            if (!slots.TryGetValue(slot, out SlotCounts counts))
            {
                if (slots.Count >= MaxSlots)
                {
                    long oldest = slots.Keys.First();
                    if (slot < oldest)
                    {
                        return false;
                    }

                    slots.Remove(oldest);
                }

                counts = default;
            }

            slots[slot] = counts.Add(outcome);
            return true;
        }
    }

    /// <inheritdoc />
    public SloSlotTotals Read(string sloId, long firstSlot, long lastSlot)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(sloId) || !_series.TryGetValue(sloId, out SortedDictionary<long, SlotCounts>? slots) || slots.Count == 0)
            {
                return SloSlotTotals.Empty;
            }

            long good = 0, bad = 0, unobserved = 0, goodSlots = 0, badSlots = 0, unobservedSlots = 0;
            foreach ((long slot, SlotCounts counts) in slots)
            {
                if (slot < firstSlot)
                {
                    continue;
                }

                if (slot > lastSlot)
                {
                    break;
                }

                good += counts.Good;
                bad += counts.Bad;
                unobserved += counts.Unobserved;
                if (counts.Bad > 0)
                {
                    badSlots++;
                }
                else if (counts.Unobserved > 0)
                {
                    unobservedSlots++;
                }
                else
                {
                    goodSlots++;
                }
            }

            return new SloSlotTotals(good, bad, unobserved, goodSlots, badSlots, unobservedSlots, slots.Keys.First());
        }
    }

    private readonly record struct SlotCounts(int Good, int Bad, int Unobserved)
    {
        // Counts saturate rather than wrap, so a flood can never turn bad samples into a negative number.
        public SlotCounts Add(SloSampleOutcome outcome) => outcome switch
        {
            SloSampleOutcome.Good => this with { Good = Bump(Good) },
            SloSampleOutcome.Bad => this with { Bad = Bump(Bad) },
            _ => this with { Unobserved = Bump(Unobserved) },
        };

        private static int Bump(int value) => value == int.MaxValue ? value : value + 1;
    }
}
