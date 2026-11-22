namespace Bazaar.Server.Setup;

/// <summary>
/// The overall status and the 0 to 100 score of a verification report (doc 45 section 1.12). Pure: the same statuses always give the
/// same answer. The weights are constants so an operator can read them here and a test can pin them.
/// </summary>
/// <remarks>
/// Overall: any red makes the overall red. Otherwise any amber or not-evaluated check makes it amber. Only all-green is green, so a
/// not-evaluated check can never make a setup look verified.
/// Score: start at 100 and subtract, per check, <see cref="RedPenalty"/> for each red, <see cref="AmberPenalty"/> for each amber and
/// <see cref="NotEvaluatedPenalty"/> for each not-evaluated check. The red total is capped at <see cref="RedPenaltyCap"/>, so many
/// reds cannot push the score below the cap. The result is floored at 0. Green checks cost nothing.
/// </remarks>
public static class SetupVerificationRules
{
    /// <summary>The score of a report with no penalties.</summary>
    public const int MaxScore = 100;

    /// <summary>The lowest score a report can have.</summary>
    public const int MinScore = 0;

    /// <summary>Penalty for each red check. WORKING DEFAULT, pending the owner's review of the weights.</summary>
    public const int RedPenalty = 40;

    /// <summary>The most the red checks can take off in total. WORKING DEFAULT: two reds reach it, so a third costs nothing more.</summary>
    public const int RedPenaltyCap = 80;

    /// <summary>Penalty for each amber check. WORKING DEFAULT.</summary>
    public const int AmberPenalty = 10;

    /// <summary>Penalty for each not-evaluated check. WORKING DEFAULT: smaller than amber, because the check did not find a problem.</summary>
    public const int NotEvaluatedPenalty = 5;

    /// <summary>The overall status of a set of check statuses. The set must not be empty.</summary>
    public static SetupCheckStatus Overall(IEnumerable<SetupCheckStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var any = false;
        var anyRed = false;
        var anyHold = false;
        foreach (var status in statuses)
        {
            any = true;
            anyRed |= status == SetupCheckStatus.Red;
            anyHold |= status is SetupCheckStatus.Amber or SetupCheckStatus.NotEvaluated;
        }

        if (!any)
        {
            throw new ArgumentException("A report needs at least one check status.", nameof(statuses));
        }

        if (anyRed)
        {
            return SetupCheckStatus.Red;
        }

        return anyHold ? SetupCheckStatus.Amber : SetupCheckStatus.Green;
    }

    /// <summary>The 0 to 100 score of a set of check statuses. See the type remarks for the rule.</summary>
    public static int Score(IEnumerable<SetupCheckStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var reds = 0;
        var ambers = 0;
        var notEvaluated = 0;
        foreach (var status in statuses)
        {
            switch (status)
            {
                case SetupCheckStatus.Red:
                    reds++;
                    break;
                case SetupCheckStatus.Amber:
                    ambers++;
                    break;
                case SetupCheckStatus.NotEvaluated:
                    notEvaluated++;
                    break;
            }
        }

        // The cap bounds the red term, so the sum stays far inside the int range for any realistic check count.
        var penalty = Math.Min(RedPenaltyCap, reds * RedPenalty) + ambers * AmberPenalty + notEvaluated * NotEvaluatedPenalty;
        return Math.Max(MinScore, MaxScore - penalty);
    }
}
