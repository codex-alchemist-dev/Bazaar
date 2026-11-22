namespace Bazaar.Kodo.Slo;

/// <summary>
/// Computes SLO status from a sample store (doc 46 section 12.3). Pure: no clock (the caller passes <c>now</c>), no I/O, no throw for
/// missing data. The window ends at the last complete slot, so the slot in progress never counts for or against a service. Unobserved
/// time is never good: it consumes budget like bad time and is counted apart.
/// </summary>
public static class SloEvaluator
{
    private const long _million = 1_000_000;

    /// <summary>Evaluates one SLO. Throws <see cref="ArgumentException"/> when the store keeps fewer slots than the window or a rule window needs.</summary>
    public static SloStatus Evaluate(SloDefinition definition, ISloSampleStore store, SloBurnPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);

        long windowSlots = SlotCount(definition.Window, store);
        long longest = SlotCount(policy.LongestWindow, store);
        if (windowSlots > store.MaxSlots || longest > store.MaxSlots)
        {
            throw new ArgumentException("The store keeps fewer slots than the window needs.", nameof(store));
        }

        long lastSlot = SloSlots.IndexOf(now, store.Resolution) - 1;
        Units window = Measure(definition, store, lastSlot, windowSlots);

        long consumed = 0;
        int remaining = 0;
        int? achieved = null;
        if (window.Total > 0)
        {
            // Availability spends the budget of the whole window, so a young series has used only what it has seen. Latency has no
            // fixed length, so its budget is a share of the samples it has.
            long budgetBase = definition.Kind == SloIndicatorKind.Availability ? windowSlots : window.Total;
            consumed = Clamp((Int128)window.NotGood * _million * _million / ((Int128)budgetBase * definition.AllowedPpm));
            remaining = (int)Math.Clamp(_million - consumed, 0, _million);
            achieved = (int)((Int128)window.Good * _million / window.Total);
        }

        List<SloBurnReading> readings = [];
        foreach (SloBurnRule rule in policy.Rules)
        {
            int? shortBurn = Burn(definition, store, lastSlot, SlotCount(rule.ShortWindow, store));
            int? longBurn = Burn(definition, store, lastSlot, SlotCount(rule.LongWindow, store));
            bool firing = shortBurn >= rule.ThresholdMilli && longBurn >= rule.ThresholdMilli;
            readings.Add(new SloBurnReading(rule.Name, rule.Severity, shortBurn, longBurn, firing));
        }

        SloHealth health;
        if (window.Total == 0)
        {
            health = SloHealth.NoData;
        }
        else if (remaining == 0 || readings.Any(r => r.Firing && r.Severity == SloBurnSeverity.Critical))
        {
            health = SloHealth.Breached;
        }
        else
        {
            health = readings.Any(r => r.Firing) ? SloHealth.Warning : SloHealth.Ok;
        }

        return new SloStatus(
            definition.Id,
            definition.Kind,
            definition.TargetPpm,
            definition.Window,
            now.ToUniversalTime(),
            health,
            window.Good,
            window.Bad,
            window.Unobserved,
            achieved,
            consumed,
            remaining,
            readings);
    }

    /// <summary>
    /// Evaluates many SLOs into one snapshot sorted by id. Throws <see cref="ArgumentException"/> for a duplicate id or an unfit store.
    /// </summary>
    public static SloSnapshot EvaluateAll(IEnumerable<SloDefinition> definitions, ISloSampleStore store, SloBurnPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        SloDefinition[] ordered = [.. definitions.OrderBy(d => d.Id, StringComparer.Ordinal)];
        for (int i = 1; i < ordered.Length; i++)
        {
            if (string.Equals(ordered[i].Id, ordered[i - 1].Id, StringComparison.Ordinal))
            {
                throw new ArgumentException("Two SLOs share one id.", nameof(definitions));
            }
        }

        return new SloSnapshot(now.ToUniversalTime(), [.. ordered.Select(d => Evaluate(d, store, policy, now))]);
    }

    private readonly record struct Units(long Good, long Bad, long Unobserved)
    {
        public long Total => Good + Bad + Unobserved;

        public long NotGood => Bad + Unobserved;
    }

    private static long SlotCount(TimeSpan span, ISloSampleStore store) => Math.Max(1, span.Ticks / store.Resolution.Ticks);

    private static Units Measure(SloDefinition definition, ISloSampleStore store, long lastSlot, long slotCount)
    {
        long firstSlot = lastSlot - slotCount + 1;
        SloSlotTotals totals = store.Read(definition.Id, firstSlot, lastSlot);
        if (definition.Kind == SloIndicatorKind.LatencyThreshold)
        {
            return new Units(totals.GoodSamples, totals.BadSamples, totals.UnobservedSamples);
        }

        if (totals.FirstSlot is null)
        {
            return default;
        }

        // Time before the first sample the store ever held is not a gap: the series did not exist yet.
        long start = Math.Max(firstSlot, totals.FirstSlot.Value);
        if (start > lastSlot)
        {
            return default;
        }

        long gaps = Math.Max(0, lastSlot - start + 1 - totals.OccupiedSlots);
        return new Units(totals.GoodSlots, totals.BadSlots, totals.UnobservedSlots + gaps);
    }

    // The burn rate in thousandths: the not-good share divided by the allowed share. Null when the window holds nothing.
    private static int? Burn(SloDefinition definition, ISloSampleStore store, long lastSlot, long slotCount)
    {
        Units units = Measure(definition, store, lastSlot, slotCount);
        if (units.Total == 0)
        {
            return null;
        }

        Int128 burn = (Int128)units.NotGood * _million * 1_000 / ((Int128)units.Total * definition.AllowedPpm);
        return (int)Int128.Min(burn, int.MaxValue);
    }

    private static long Clamp(Int128 value) => (long)Int128.Min(value, long.MaxValue);
}
