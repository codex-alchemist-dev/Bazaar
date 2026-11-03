namespace Bazaar.Server.Council.Pilot;

/// <summary>
/// Pilot settings (doc 10, Settings table). Defaults are the doc's values. The Bazaar.Server operator sets them;
/// they are audited changes, not per-vote inputs.
/// </summary>
/// <param name="HoldoutShare">council.pilot_holdout_share: share of votes that skip follow-up. Between 0 and 1.</param>
/// <param name="StopFlipShare">council.pilot_stop_flip_share: evidence-free share of flips above which follow-up stops. Between 0 and 1.</param>
/// <param name="TargetVotes">council.pilot_target_votes: votes needed before the stop rule is evaluated. At least 1.</param>
public sealed record PilotSettings(decimal HoldoutShare = 0.15m, decimal StopFlipShare = 0.20m, int TargetVotes = 20)
{
    /// <summary>Returns one message per invalid setting. An empty list means the settings are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (HoldoutShare < 0m || HoldoutShare > 1m)
        {
            errors.Add("council.pilot_holdout_share must be between 0 and 1.");
        }

        if (StopFlipShare < 0m || StopFlipShare > 1m)
        {
            errors.Add("council.pilot_stop_flip_share must be between 0 and 1.");
        }

        if (TargetVotes < 1)
        {
            errors.Add("council.pilot_target_votes must be at least 1.");
        }

        return errors;
    }
}
