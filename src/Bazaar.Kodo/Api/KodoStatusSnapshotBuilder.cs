using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Api;

/// <summary>One component's reading as the probe runtime reports it, before it becomes a <see cref="KodoComponentDto"/>.</summary>
/// <param name="Id">The probe or category id. Must be non-blank and unique within a snapshot.</param>
/// <param name="Status">The component's status.</param>
/// <param name="Cause">The cause code. Required when <paramref name="Status"/> is not green, and dropped when it is green.</param>
/// <param name="Since">When the component entered this status, in UTC. Not after the snapshot's generation time.</param>
public sealed record KodoComponentInput(string Id, ProbeStatus Status, string? Cause, DateTimeOffset Since);

/// <summary>
/// Builds the status snapshot from the evaluator's output (doc 46 sections 8 and 12). Pure: the same inputs always give the same
/// snapshot, and components are sorted by id so the encoding is stable.
/// </summary>
public static class KodoStatusSnapshotBuilder
{
    /// <summary>
    /// Builds a snapshot.
    /// </summary>
    /// <param name="evaluation">The evaluator's output. The displayed state and the underlying state are taken from it.</param>
    /// <param name="observation">The observation the state and score were computed from. Its critical facts set the score caps of section 12.2.</param>
    /// <param name="score">The health score from <see cref="KodoScore.Compute"/>, from 0 to 100. Must respect the caps of section 12.2 (see remarks).</param>
    /// <param name="components">The component readings. Ids must be unique.</param>
    /// <param name="revision">The stream revision the snapshot reflects. Not negative.</param>
    /// <param name="generatedAt">When the snapshot is built, in UTC.</param>
    /// <param name="journalHead">The journal head hash, or null when the journal has no records yet. Must be a full hash when given.</param>
    /// <param name="controls">The controls this caller may see, decided by the server. Defaults to none.</param>
    /// <remarks>
    /// A score above the cap is rejected, never clamped, so a broken caller cannot publish a healthy-looking number (section 12.2):
    /// a critical red probe caps at 49, an integrity or contract failure at 10, a critical unknown at 70, and an underlying
    /// <see cref="KodoState.Failing"/> state at 49. The lowest applicable cap wins.
    /// </remarks>
    /// <exception cref="ArgumentException">The score is above a cap, a component is invalid, or the journal head is not a full hash.</exception>
    public static KodoStatusSnapshotDto Build(
        KodoEvaluation evaluation,
        KodoObservation observation,
        int score,
        IEnumerable<KodoComponentInput> components,
        long revision,
        DateTimeOffset generatedAt,
        byte[]? journalHead,
        IEnumerable<KodoControlDto>? controls = null)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(components);

        if (score is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(score), score, "The score must be from 0 to 100.");
        }

        int cap = KodoScore.CapFor(observation);
        if (evaluation.Underlying == KodoState.Failing)
        {
            cap = Math.Min(cap, KodoScore.CriticalRedCap);
        }

        if (score > cap)
        {
            throw new ArgumentException($"The score {score} is above the cap of {cap} that applies to this state (doc 46 section 12.2).", nameof(score));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "The revision must not be negative.");
        }

        if (journalHead is not null && journalHead.Length != JournalChain.HashLength)
        {
            throw new ArgumentException($"The journal head must be a {JournalChain.HashLength}-byte hash.", nameof(journalHead));
        }

        List<KodoComponentInput> sorted = [];
        foreach (KodoComponentInput? entry in components)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(components));
            sorted.Add(entry);
        }

        List<KodoComponentDto> built = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (KodoComponentInput input in sorted.OrderBy(component => component.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(input.Id))
            {
                throw new ArgumentException("A component id must not be blank.", nameof(components));
            }

            if (!ids.Add(input.Id))
            {
                throw new ArgumentException($"The component id '{input.Id}' appears more than once.", nameof(components));
            }

            if (input.Status != ProbeStatus.Green && string.IsNullOrEmpty(input.Cause))
            {
                throw new ArgumentException($"The component '{input.Id}' is not green, so it needs a cause.", nameof(components));
            }

            if (input.Since > generatedAt)
            {
                throw new ArgumentException($"The component '{input.Id}' entered its state after the snapshot was generated.", nameof(components));
            }

            string? cause = input.Status == ProbeStatus.Green ? null : input.Cause;
            built.Add(new KodoComponentDto(input.Id, KodoWireNames.Probe(input.Status), cause, input.Since));
        }

        KodoStatusDto status = new(
            KodoWireNames.State(evaluation.State),
            evaluation.State == KodoState.Frozen ? KodoWireNames.State(evaluation.Underlying) : null,
            score,
            generatedAt,
            controls is null ? [] : controls.ToArray());

        return new KodoStatusSnapshotDto(
            KodoStatusSnapshotDto.CurrentSchemaVersion,
            revision,
            status,
            journalHead is null ? null : Convert.ToHexString(journalHead).ToLowerInvariant(),
            built.AsReadOnly());
    }
}
