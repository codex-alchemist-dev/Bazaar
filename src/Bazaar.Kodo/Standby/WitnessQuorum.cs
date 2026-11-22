namespace Bazaar.Kodo.Standby;

/// <summary>What one witness says about the primary.</summary>
public enum WitnessAnswer
{
    /// <summary>The witness did not answer, timed out or failed. Never counts for or against anything.</summary>
    NoAnswer,

    /// <summary>The witness can reach the primary and sees it renewing.</summary>
    PrimaryReachable,

    /// <summary>The witness cannot reach the primary.</summary>
    PrimaryUnreachable,
}

/// <summary>One witness's report: its answer and the newest lease it knows of.</summary>
public sealed record WitnessReport(string WitnessId, WitnessAnswer Answer, LeaseRecord? HighestLease);

/// <summary>The count of a quorum check.</summary>
/// <param name="Agreeing">Distinct known witnesses that agree.</param>
/// <param name="Required">How many must agree.</param>
/// <param name="Reachable">Distinct known witnesses that see the primary (only for a report tally).</param>
/// <param name="Met">True when <paramref name="Agreeing"/> reaches <paramref name="Required"/>.</param>
public sealed record QuorumResult(int Agreeing, int Required, int Reachable, bool Met);

/// <summary>
/// The witness set and how many must agree (doc 46 section 7.6). The default is a strict majority: 2 of 3 with the owner's paired
/// devices plus one optional third node, or 2 of 2 when only paired devices exist. The floor is a strict majority, so two disjoint
/// groups can never both reach quorum. A setting may raise the count, never lower it below the floor.
/// </summary>
public sealed class WitnessQuorumPolicy
{
    private readonly HashSet<string> _ids;

    /// <summary>Creates a policy. <paramref name="required"/> defaults to the strict majority.</summary>
    /// <exception cref="ArgumentException">Fewer than 2 witnesses, an empty or duplicate id, or a count outside the strict majority up to all witnesses.</exception>
    public WitnessQuorumPolicy(IEnumerable<string> witnessIds, int? required = null)
    {
        ArgumentNullException.ThrowIfNull(witnessIds);
        string[] ids = witnessIds.ToArray();
        if (ids.Length < 2 || ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A witness quorum needs at least two witnesses with non-empty ids.", nameof(witnessIds));
        }

        _ids = new HashSet<string>(ids, StringComparer.Ordinal);
        if (_ids.Count != ids.Length)
        {
            throw new ArgumentException("Witness ids must be distinct.", nameof(witnessIds));
        }

        int floor = StrictMajority(ids.Length);
        int chosen = required ?? floor;
        if (chosen < floor || chosen > ids.Length)
        {
            throw new ArgumentException($"The quorum must be between the strict majority ({floor}) and all {ids.Length} witnesses.", nameof(required));
        }

        WitnessIds = ids;
        Required = chosen;
    }

    /// <summary>The witness ids, in the order given.</summary>
    public IReadOnlyList<string> WitnessIds { get; }

    /// <summary>How many witnesses must agree.</summary>
    public int Required { get; }

    /// <summary>The smallest group that is more than half of <paramref name="count"/>.</summary>
    public static int StrictMajority(int count) => (count / 2) + 1;

    /// <summary>True when <paramref name="witnessId"/> belongs to this set.</summary>
    public bool Contains(string witnessId) => _ids.Contains(witnessId);

    /// <summary>
    /// Tallies reports. Each witness counts once. A witness that is not in the set is ignored, and one that reports twice with
    /// different answers counts as no answer. Only <see cref="WitnessAnswer.PrimaryUnreachable"/> counts toward agreement.
    /// </summary>
    public QuorumResult EvaluateUnreachable(IEnumerable<WitnessReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        Dictionary<string, WitnessAnswer> answers = new(StringComparer.Ordinal);
        foreach (WitnessReport report in reports)
        {
            if (report is null || !Contains(report.WitnessId))
            {
                continue;
            }

            answers[report.WitnessId] = answers.TryGetValue(report.WitnessId, out WitnessAnswer seen) && seen != report.Answer
                ? WitnessAnswer.NoAnswer
                : report.Answer;
        }

        int unreachable = answers.Count(pair => pair.Value == WitnessAnswer.PrimaryUnreachable);
        int reachable = answers.Count(pair => pair.Value == WitnessAnswer.PrimaryReachable);
        return new QuorumResult(unreachable, Required, reachable, unreachable >= Required);
    }

    /// <summary>Tallies the witnesses that granted a lease. Each known witness counts once; others are ignored.</summary>
    public QuorumResult EvaluateGrants(IEnumerable<string> grantingWitnessIds)
    {
        ArgumentNullException.ThrowIfNull(grantingWitnessIds);
        int granted = grantingWitnessIds.Where(id => id is not null && Contains(id)).Distinct(StringComparer.Ordinal).Count();
        return new QuorumResult(granted, Required, 0, granted >= Required);
    }
}
