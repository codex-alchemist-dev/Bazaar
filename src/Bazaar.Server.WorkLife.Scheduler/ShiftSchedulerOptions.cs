using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.WorkLife.Scheduler;

/// <summary>
/// Operator settings for the shift scheduler (docs/design/09). They bind from <c>Bazaar:WorkLifeScheduler</c>; the settings registry
/// takes over the <c>schedule.*</c> keys once a module can read it. The defaults are safe: the scheduler runs, but with no
/// agent registered in its source it does nothing.
/// </summary>
public sealed class ShiftSchedulerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bazaar:WorkLifeScheduler";

    /// <summary>Turns the scheduler loop off without removing the component.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often every agent is evaluated. Shift boundaries are on minutes, so 30 seconds is plenty. [Not in the doc: an implementation choice.]</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><c>schedule.calendar_availability_kinds</c>: extra calendar event kinds that count as availability events (default none).</summary>
    public IReadOnlyList<string> CalendarAvailabilityKinds { get; set; } = [];
}

/// <summary>Reads and checks <see cref="ShiftSchedulerOptions"/>. A value that does not parse or is out of range fails at start.</summary>
public static class ShiftSchedulerOptionsReader
{
    /// <summary>Reads the section; absent keys keep the defaults.</summary>
    /// <exception cref="InvalidOperationException">A value does not parse or is out of range.</exception>
    public static ShiftSchedulerOptions Read(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var o = new ShiftSchedulerOptions();
        if (section["Enabled"] is { Length: > 0 } enabled)
        {
            o.Enabled = bool.TryParse(enabled, out var b)
                ? b
                : throw new InvalidOperationException($"{ShiftSchedulerOptions.SectionName}:Enabled must be true or false.");
        }

        if (section["TickIntervalS"] is { Length: > 0 } tick)
        {
            o.TickInterval = double.TryParse(tick, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s)
                ? TimeSpan.FromSeconds(s)
                : throw new InvalidOperationException($"{ShiftSchedulerOptions.SectionName}:TickIntervalS must be a number of seconds.");
        }

        if (section["CalendarAvailabilityKinds"] is { Length: > 0 } kinds)
        {
            o.CalendarAvailabilityKinds = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        Validate(o);
        return o;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when a value is out of range.</summary>
    public static void Validate(ShiftSchedulerOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.TickInterval <= TimeSpan.Zero || o.TickInterval > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException($"{ShiftSchedulerOptions.SectionName}:TickIntervalS must be above 0 and at most 3600.");
        }
    }
}
