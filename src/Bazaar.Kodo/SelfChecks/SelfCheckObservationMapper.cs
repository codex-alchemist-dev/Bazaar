using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.SelfChecks;

/// <summary>One self-check as the status view shows it.</summary>
/// <param name="DefinitionId">The definition.</param>
/// <param name="Status">Green, amber or red.</param>
/// <param name="Cause">The stable cause code.</param>
public sealed record SelfCheckLine(string DefinitionId, SelfCheckStatus Status, string Cause);

/// <summary>The observation for the evaluator plus one line per enabled check.</summary>
public sealed record SelfCheckAssessment(KodoObservation Observation, IReadOnlyList<SelfCheckLine> Lines);

/// <summary>
/// Maps self-check history into the evaluator's <see cref="KodoObservation"/> (doc 46 sections 8.2 and 17.6). Never green on doubt: a
/// check with no result past its first due time plus grace, a result older than one interval plus grace, a skip, a timeout or a runner
/// fault is amber, and a failure is red. A checksum mismatch is an integrity finding (section 17.2). Facts that are not self-check
/// facts are left at their defaults; merge with <see cref="Merge"/>, which only ever raises a flag.
/// </summary>
public static class SelfCheckObservationMapper
{
    /// <summary>Assesses every enabled definition at <paramref name="now"/>.</summary>
    public static SelfCheckAssessment Assess(IReadOnlyList<SelfCheckDefinition> definitions, SelfCheckHistory history, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(history);
        bool integrity = false;
        bool amberBeyond = false;
        bool within = false;
        int redBeyond = 0;
        List<SelfCheckLine> lines = [];

        foreach (SelfCheckDefinition definition in definitions)
        {
            if (definition is null || !definition.Enabled)
            {
                continue;
            }

            IReadOnlyList<SelfCheckResult> recent = history.Recent(definition.Id);
            SelfCheckResult? latest = recent.Count > 0 ? recent[^1] : null;
            if (latest is null)
            {
                if (now < definition.Schedule.Anchor)
                {
                    continue;
                }

                // Nothing has run yet. Inside the grace after the first due time it is within grace; after it, amber beyond grace.
                if (now - definition.Schedule.Anchor > definition.Grace)
                {
                    amberBeyond = true;
                }
                else
                {
                    within = true;
                }

                lines.Add(new SelfCheckLine(definition.Id, SelfCheckStatus.Amber, SelfCheckCauses.NeverRun));
                continue;
            }

            if (latest.Status == SelfCheckStatus.Green)
            {
                if (now - latest.FinishedAt > definition.Schedule.Interval + definition.Grace)
                {
                    amberBeyond = true;
                    lines.Add(new SelfCheckLine(definition.Id, SelfCheckStatus.Amber, SelfCheckCauses.Stale));
                }
                else
                {
                    lines.Add(new SelfCheckLine(definition.Id, SelfCheckStatus.Green, SelfCheckCauses.Ok));
                }

                continue;
            }

            lines.Add(new SelfCheckLine(definition.Id, latest.Status, latest.Cause));
            if (latest.Kind == SelfCheckKind.ChecksumSweep && latest.Outcome == SelfCheckOutcome.Failed)
            {
                integrity = true;
                continue;
            }

            bool escalated = now - StreakStart(recent) >= definition.Grace;
            if (latest.Status == SelfCheckStatus.Red)
            {
                if (escalated)
                {
                    redBeyond++;
                }
                else
                {
                    within = true;
                }
            }
            else if (escalated)
            {
                amberBeyond = true;
            }
            else
            {
                within = true;
            }
        }

        KodoObservation observation = new()
        {
            IntegrityOrContractFailure = integrity,
            NonCriticalRedBeyondGrace = redBeyond,
            NonCriticalAmberBeyondGrace = amberBeyond,
            NonCriticalWithinGrace = within,
        };
        return new SelfCheckAssessment(observation, lines);
    }

    /// <summary>Combines two observations by raising flags and adding counts. The result is never greener than either input.</summary>
    public static KodoObservation Merge(KodoObservation baseObservation, KodoObservation selfChecks)
    {
        ArgumentNullException.ThrowIfNull(baseObservation);
        ArgumentNullException.ThrowIfNull(selfChecks);
        return baseObservation with
        {
            IntegrityOrContractFailure = baseObservation.IntegrityOrContractFailure || selfChecks.IntegrityOrContractFailure,
            NonCriticalRedBeyondGrace = baseObservation.NonCriticalRedBeyondGrace + selfChecks.NonCriticalRedBeyondGrace,
            NonCriticalAmberBeyondGrace = baseObservation.NonCriticalAmberBeyondGrace || selfChecks.NonCriticalAmberBeyondGrace,
            NonCriticalWithinGrace = baseObservation.NonCriticalWithinGrace || selfChecks.NonCriticalWithinGrace,
        };
    }

    // The start of the trailing run of results that are not green (the oldest one kept if the whole history is not green).
    private static DateTimeOffset StreakStart(IReadOnlyList<SelfCheckResult> recent)
    {
        int index = recent.Count - 1;
        while (index > 0 && recent[index - 1].Status != SelfCheckStatus.Green)
        {
            index--;
        }

        return recent[index].FinishedAt;
    }
}
