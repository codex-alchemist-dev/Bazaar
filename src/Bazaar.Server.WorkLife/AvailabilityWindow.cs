namespace Bazaar.Server.WorkLife;

/// <summary>
/// The schedule's answer at one instant.
/// </summary>
/// <param name="State">Availability at the evaluated instant.</param>
/// <param name="Reason">Why: <c>shift</c>, <c>curfew</c>, <c>outside-shift</c> or <c>vacation:&lt;event id&gt;</c>.</param>
/// <param name="NextChangeAt">Earliest schedule boundary strictly after the instant, within 8 days; null if none.</param>
public sealed record AvailabilityWindow(AvailabilityState State, string Reason, DateTimeOffset? NextChangeAt);
