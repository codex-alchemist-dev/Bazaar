namespace Bazaar.Client.Core.Tour;

/// <summary>One step of an interactive tour. Text is held as keys so every head can localise it (doc 41 accessibility, i18n from day one).</summary>
/// <param name="Id">Stable step id, unique within the tour.</param>
/// <param name="TitleKey">Localisation key of the step title.</param>
/// <param name="BodyKey">Localisation key of the step body.</param>
/// <param name="Anchor">Optional mnemonic or pane id the step points at. Heads highlight it when it exists.</param>
public sealed record TourStep(string Id, string TitleKey, string BodyKey, string? Anchor = null);

/// <summary>
/// A named tour, for example the first-run tour. Immutable once created. Create it with <see cref="Create"/>,
/// which checks the step list.
/// </summary>
public sealed class TourDefinition
{
    private TourDefinition(string id, IReadOnlyList<TourStep> steps)
    {
        Id = id;
        Steps = steps;
    }

    public string Id { get; }

    public IReadOnlyList<TourStep> Steps { get; }

    /// <summary>Creates a tour. Throws <see cref="ArgumentException"/> for a blank id, no steps, or duplicate step ids.</summary>
    public static TourDefinition Create(string id, IEnumerable<TourStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(steps);

        var list = steps.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A tour needs at least one step.", nameof(steps));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TourStep step in list)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.TitleKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.BodyKey);
            if (!seen.Add(step.Id))
            {
                throw new ArgumentException($"Duplicate tour step id '{step.Id}'.", nameof(steps));
            }
        }

        return new TourDefinition(id, list);
    }
}
