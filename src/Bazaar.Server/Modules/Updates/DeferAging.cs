namespace Bazaar.Server.Modules.Updates;

/// <summary>How long a deferred candidate has waited, and whether that is past the warning threshold.</summary>
/// <param name="AgeDays">Whole days waited, rounded down. Zero when the deferral time is in the future.</param>
/// <param name="IsAging">True when the wait is longer than the configured warning threshold.</param>
public sealed record DeferAgeReport(int AgeDays, bool IsAging);

/// <summary>
/// Reports how long a deferred update has aged (doc 42 section 10.2). The module manager and Kodo show an aging dep, and
/// the owner can accept the age, but it is never hidden. The threshold is passed in as a value; this type reads no settings.
/// </summary>
public sealed class DeferAging
{
    /// <summary>The setting key that holds the warning threshold in days. Read by the caller, not here.</summary>
    public const string DeferWarnDaysSettingKey = "deps.update.deferWarnDays";

    /// <summary>The shipped default for <see cref="DeferWarnDaysSettingKey"/>.</summary>
    public const int DefaultDeferWarnDays = 30;

    private readonly TimeProvider _timeProvider;

    public DeferAging(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Measures a deferral that started at <paramref name="deferredSince"/>. The warning fires when the wait is strictly longer
    /// than <paramref name="deferWarnDays"/> days.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The threshold is negative. The settings layer rejects that value first.</exception>
    public DeferAgeReport Measure(DateTimeOffset deferredSince, int deferWarnDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deferWarnDays);

        var elapsed = _timeProvider.GetUtcNow() - deferredSince;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var ageDays = (int)Math.Min(int.MaxValue, Math.Floor(elapsed.TotalDays));
        return new DeferAgeReport(ageDays, elapsed > TimeSpan.FromDays(deferWarnDays));
    }
}
