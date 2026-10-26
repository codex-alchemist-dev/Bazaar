using System.Collections.Immutable;

namespace Bazaar.Server.WorkLife;

/// <summary>
/// Operator-set additions to the evaluation. Only the value that the install setting
/// <c>schedule.calendar_availability_kinds</c> supplies belongs here; the default adds no kinds.
/// </summary>
public sealed record EvaluationOptions
{
    /// <summary>
    /// Event kinds that count as vacation even when <see cref="CalendarEvent.AffectsAvailability"/> is false.
    /// Matching is ordinal and case-sensitive.
    /// </summary>
    public IReadOnlySet<string> ExtraAvailabilityKinds { get; init; } = ImmutableHashSet<string>.Empty;
}
