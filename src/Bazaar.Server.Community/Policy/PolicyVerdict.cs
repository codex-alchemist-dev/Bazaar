namespace Bazaar.Server.Community.Policy;

/// <summary>The two results of a policy check. Deny is the default for anything not explicitly allowed.</summary>
public enum PolicyOutcome
{
    Allow,
    Deny,
}

/// <summary>The outcome of <see cref="PolicyResolver.Decide"/>, with the reason written to the audit trail and the draft review.</summary>
/// <param name="Outcome">Allow or Deny.</param>
/// <param name="Reason">A short, human-readable reason. Never empty.</param>
public sealed record PolicyVerdict(PolicyOutcome Outcome, string Reason)
{
    /// <summary>True only for <see cref="PolicyOutcome.Allow"/>.</summary>
    public bool IsAllowed => Outcome == PolicyOutcome.Allow;

    /// <summary>An Allow verdict with the given reason.</summary>
    public static PolicyVerdict Allowed(string reason) => new(PolicyOutcome.Allow, reason);

    /// <summary>A Deny verdict with the given reason.</summary>
    public static PolicyVerdict Denied(string reason) => new(PolicyOutcome.Deny, reason);
}

/// <summary>
/// Pure policy resolver (doc 36, "Behaviour and edge cases"). No I/O and no clock: the caller passes <c>today</c>.
/// Rules, in order:
/// <list type="number">
/// <item>No record (unknown platform) is Deny.</item>
/// <item>Banned is Deny at every tier, including Read.</item>
/// <item>Unverified is Deny at every tier, including Read.</item>
/// <item>Secondary allows Read only. Every other action is Deny.</item>
/// <item>Verified allows only the actions in <see cref="PlatformPolicy.Allowed"/>.</item>
/// <item>An Allow from a Verified or Secondary record whose <c>CheckedOn</c> is more than 30 days before <c>today</c> is Deny until the platform is re-checked.</item>
/// </list>
/// CreateAccount and OutreachEmail go through the same rules. They are allowed only when a record lists them, which no
/// seeded record does. Those decisions are human or Tier 2 decisions made elsewhere.
/// </summary>
public static class PolicyResolver
{
    /// <summary>Age in days after which a Verified or Secondary record stops allowing anything (doc 36).</summary>
    public const int MaxCheckedAgeDays = 30;

    /// <summary>Decides whether <paramref name="action"/> may proceed on <paramref name="policy"/> on <paramref name="today"/>.</summary>
    public static PolicyVerdict Decide(PlatformPolicy? policy, CommunityAction action, DateOnly today)
    {
        if (policy is null)
        {
            return PolicyVerdict.Denied($"Unknown platform: no platform_policy record for {action}.");
        }

        switch (policy.Status)
        {
            case PolicyStatus.Banned:
                return PolicyVerdict.Denied($"{policy.Platform} bans agent posting; {action} is denied at every tier.");

            case PolicyStatus.Unverified:
                return PolicyVerdict.Denied($"{policy.Platform} rule is not verified; {action} is denied.");

            case PolicyStatus.Secondary:
                if (action != CommunityAction.Read)
                {
                    return PolicyVerdict.Denied($"{policy.Platform} is only secondary-sourced; {action} is denied (Read only).");
                }

                return WithFreshness(policy, today, $"{policy.Platform} is secondary-sourced; Read is allowed.");

            case PolicyStatus.Verified:
                if (!policy.Allowed.Contains(action))
                {
                    return PolicyVerdict.Denied($"{action} is not in the verified allowed actions for {policy.Platform}.");
                }

                return WithFreshness(policy, today, $"{action} is allowed on {policy.Platform} (verified).");

            default:
                return PolicyVerdict.Denied($"{policy.Platform} has an unrecognised policy status; {action} is denied.");
        }
    }

    /// <summary>Turns an Allow into a Deny when the record is older than <see cref="MaxCheckedAgeDays"/>.</summary>
    private static PolicyVerdict WithFreshness(PlatformPolicy policy, DateOnly today, string allowReason)
    {
        if (policy.CheckedOn < today.AddDays(-MaxCheckedAgeDays))
        {
            return PolicyVerdict.Denied(
                $"{policy.Platform} policy was checked on {policy.CheckedOn:yyyy-MM-dd}, more than {MaxCheckedAgeDays} days before {today:yyyy-MM-dd}; re-check before posting.");
        }

        return PolicyVerdict.Allowed(allowReason);
    }
}
