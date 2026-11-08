using System.Text.RegularExpressions;

namespace Bazaar.Server.Community.Gates;

/// <summary>One failed quality gate. <see cref="Gate"/> is a stable snake_case key; <see cref="Reason"/> is for the sponsor.</summary>
public sealed record GateFailure(string Gate, string Reason);

/// <summary>Outcome of <see cref="QualityGates.Evaluate"/>. Passed is true only when <see cref="Failures"/> is empty.</summary>
public sealed record GateResult(bool Passed, IReadOnlyList<GateFailure> Failures);

/// <summary>
/// Quality-gate checks that must pass before any Tier 2 community post (doc 36, "Quality gates before any Tier 2 post").
/// Pure logic: no I/O, no clock (the caller passes <c>today</c>).
/// </summary>
/// <remarks>
/// Covered here: gate 1 (duplicate search, "duplicate_search"), gate 2 (example evidence, "example_evidence"),
/// gate 3 (target rules: "rule_freshness", "rule_unverified", "ai_contribution_bar") and gate 5 (personal data,
/// secrets and internal links: "personal_data", "internal_link").
/// Not covered here, by design: gate 4 (sponsor approves the exact body hash) belongs to the doc 15 approval flow,
/// and gate 6 (short, specific text, no filler) is a judgement left to the sponsor and the drafting agent.
/// All failures are collected, not just the first. Every check is a conservative heuristic: a pass is not proof
/// that the text is safe, and a failure is a reason to stop and review, not a verdict.
/// </remarks>
public static partial class QualityGates
{
    /// <summary>Maximum age of a platform policy's <c>checked_on</c> date before posting is denied (doc 36, behaviour and edge cases).</summary>
    public const int MaxRuleAgeDays = 30;

    /// <summary>Runs gates 1, 2, 3 and 5 against a draft and its platform policy. A null policy fails rule freshness.</summary>
    /// <param name="draft">The draft to check.</param>
    /// <param name="policy">The platform's policy record, or null when none exists.</param>
    /// <param name="today">The evaluation date, supplied by the caller so the result is deterministic.</param>
    public static GateResult Evaluate(DraftPost draft, PlatformPolicy? policy, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var failures = new List<GateFailure>();
        CheckDuplicateSearch(draft, failures);
        CheckExampleEvidence(draft, failures);
        CheckTargetRules(policy, today, failures);
        CheckPersonalDataAndSecrets(draft.Body, failures);

        return new GateResult(failures.Count == 0, failures);
    }

    /// <summary>Gate 1: at least one duplicate search query and at least one checked link.</summary>
    private static void CheckDuplicateSearch(DraftPost draft, List<GateFailure> failures)
    {
        bool hasQuery = draft.DuplicateSearchQueries.Any(q => !string.IsNullOrWhiteSpace(q));
        bool hasCheckedLink = draft.DuplicateSearchLinksChecked.Any(l => !string.IsNullOrWhiteSpace(l));

        if (!hasQuery || !hasCheckedLink)
        {
            failures.Add(new GateFailure(
                "duplicate_search",
                "run a duplicate search on the platform and record the queries and the existing threads checked"));
        }
    }

    /// <summary>Gate 2: a bug report needs its own example output and the versions that produced it. No example, no report.</summary>
    private static void CheckExampleEvidence(DraftPost draft, List<GateFailure> failures)
    {
        if (!draft.IsBugReport)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.ExampleOutput) || string.IsNullOrWhiteSpace(draft.ExampleVersions))
        {
            failures.Add(new GateFailure(
                "example_evidence",
                "a bug report needs a minimal example run in the agent VM, with its versions and actual output"));
        }
    }

    /// <summary>
    /// Gate 3: the policy must exist, be Verified, be checked within <see cref="MaxRuleAgeDays"/> days, and its
    /// disclosure rule must not carry an AI-contribution bar.
    /// </summary>
    private static void CheckTargetRules(PlatformPolicy? policy, DateOnly today, List<GateFailure> failures)
    {
        if (policy is null)
        {
            failures.Add(new GateFailure(
                "rule_freshness",
                "no platform policy record exists, so the target rules cannot be checked"));
            return;
        }

        int ageDays = today.DayNumber - policy.CheckedOn.DayNumber;
        if (ageDays < 0 || ageDays > MaxRuleAgeDays)
        {
            failures.Add(new GateFailure(
                "rule_freshness",
                $"platform rules were checked on {policy.CheckedOn:yyyy-MM-dd}, outside the {MaxRuleAgeDays}-day window; re-check them"));
        }

        if (policy.Status != PolicyStatus.Verified)
        {
            failures.Add(new GateFailure(
                "rule_unverified",
                $"platform rules have status {policy.Status}; only Verified rules allow posting"));
        }

        // Conservative string heuristic over the disclosure rule text only. It can miss a bar worded differently,
        // and it can flag a rule that merely mentions AI. A hit denies posting; a miss is not a clearance.
        if (ContainsAiContributionBar(policy.DisclosureRule))
        {
            failures.Add(new GateFailure(
                "ai_contribution_bar",
                "the platform's disclosure rule appears to bar AI-generated or AI-edited contributions"));
        }
    }

    private static bool ContainsAiContributionBar(string disclosureRule)
    {
        if (string.IsNullOrEmpty(disclosureRule))
        {
            return false;
        }

        return disclosureRule.Contains("no AI", StringComparison.OrdinalIgnoreCase)
            || disclosureRule.Contains("AI-generated", StringComparison.OrdinalIgnoreCase)
            || disclosureRule.Contains("generated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gate 5: no personal data, credentials or internal hosts in the outbound body.</summary>
    private static void CheckPersonalDataAndSecrets(string body, List<GateFailure> failures)
    {
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        if (EmailPattern().IsMatch(body))
        {
            failures.Add(new GateFailure("personal_data", "the body contains an email address"));
        }

        if (PhoneLikeCandidatePattern().Matches(body).Any(m => IsPhoneLike(m.Value)))
        {
            failures.Add(new GateFailure("personal_data", "the body contains a phone-like number (10 or more digits with separators)"));
        }

        if (TokenPattern().IsMatch(body))
        {
            failures.Add(new GateFailure("personal_data", "the body contains a token-like credential string"));
        }

        if (InternalHostPattern().IsMatch(body))
        {
            failures.Add(new GateFailure("internal_link", "the body links to a localhost, private or internal host"));
        }
    }

    /// <summary>A candidate has enough digits to be a phone number. The regex only finds candidates with separators.</summary>
    private static bool IsPhoneLike(string candidate) => candidate.Count(char.IsAsciiDigit) >= 10;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    // Digit groups joined by at least one separator (space, dot, dash, brackets). IsPhoneLike then counts the digits.
    [GeneratedRegex(@"(?<![\w+])\+?\d+(?:[ .()\-]+\d+)+(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneLikeCandidatePattern();

    // Known credential prefixes followed by 16 or more token characters. The hyphen is allowed so sk-proj-... keys match.
    [GeneratedRegex(@"(?<![\w-])(?:ghp_|github_pat_|sk-|AKIA)[\w-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    // Host names only: localhost, loopback, RFC 1918 ranges and *.internal / *.local. Bare "10." is matched as a
    // full IPv4 address so that version numbers such as 10.0.100 are not flagged.
    [GeneratedRegex(
        @"\blocalhost\b|\b127\.0\.0\.1\b|\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b|\b192\.168\.\d{1,3}\.\d{1,3}\b|\b[\w-]+\.(?:internal|local)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InternalHostPattern();
}
