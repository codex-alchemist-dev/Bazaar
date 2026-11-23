namespace Bazaar.Kodo.Slo;

/// <summary>
/// What a store holds for a run of slots. Sample counts are for the count-based (latency) indicator; slot counts are for the time-based
/// (availability) indicator, where each occupied slot has one worst-wins verdict.
/// </summary>
/// <param name="GoodSamples">Good samples in the range.</param>
/// <param name="BadSamples">Bad samples in the range.</param>
/// <param name="UnobservedSamples">Unobserved samples in the range.</param>
/// <param name="GoodSlots">Slots whose samples were all good.</param>
/// <param name="BadSlots">Slots with at least one bad sample.</param>
/// <param name="UnobservedSlots">Slots with no bad sample and at least one unobserved sample.</param>
/// <param name="FirstSlot">The oldest slot the store still holds for the series (not limited to the range), or null for an empty or unknown series.</param>
public readonly record struct SloSlotTotals(
    long GoodSamples,
    long BadSamples,
    long UnobservedSamples,
    long GoodSlots,
    long BadSlots,
    long UnobservedSlots,
    long? FirstSlot)
{
    /// <summary>No data.</summary>
    public static SloSlotTotals Empty => default;

    /// <summary>Slots that hold at least one sample.</summary>
    public long OccupiedSlots => GoodSlots + BadSlots + UnobservedSlots;
}

/// <summary>Maps instants to slot numbers: whole slots counted from the UTC tick origin, so every store agrees.</summary>
public static class SloSlots
{
    /// <summary>The slot that holds the instant. UTC ticks are never negative, so neither is the slot number.</summary>
    public static long IndexOf(DateTimeOffset at, TimeSpan resolution) => at.UtcTicks / resolution.Ticks;
}

/// <summary>
/// A compact, bounded store of SLO samples (doc 46 section 12.3). It keeps counts per series and slot, never raw samples, so its size
/// does not depend on the sample rate. Implementations never throw for bad input: they refuse it.
/// </summary>
public interface ISloSampleStore
{
    /// <summary>The slot width.</summary>
    TimeSpan Resolution { get; }

    /// <summary>The most slots kept for one series. Older slots are dropped.</summary>
    int MaxSlots { get; }

    /// <summary>
    /// Records one sample. Returns false, and changes nothing, for an id that is not an identifier, an outcome that is not defined, a
    /// sample older than the oldest slot kept when the series is full, or a new series beyond the series limit.
    /// </summary>
    bool Record(string sloId, DateTimeOffset at, SloSampleOutcome outcome);

    /// <summary>Totals for slots <paramref name="firstSlot"/> to <paramref name="lastSlot"/>, both included. An unknown series gives <see cref="SloSlotTotals.Empty"/>.</summary>
    SloSlotTotals Read(string sloId, long firstSlot, long lastSlot);
}
