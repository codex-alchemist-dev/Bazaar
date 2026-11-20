using System.Globalization;
using System.Text;

namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// Builds the ballot prompt for one voter (docs/design/10-council-and-voting.md, Lifecycle steps 2 and 4).
/// The independent prompt holds the motion and the voter's instructions only. The follow-up prompt adds the revealed
/// reasons under aliases and the voter's own independent ballot. Every built prompt passes <see cref="PrivacyGuard"/>
/// before it is returned.
/// </summary>
public static class BallotPromptBuilder
{
    private const string _approveRejectDescription = "approve or reject; choose yes, no or abstain.";

    /// <summary>Builds the prompt.</summary>
    /// <exception cref="ArgumentException">The request is malformed for its phase (see the request docs).</exception>
    /// <exception cref="CouncilPrivacyViolationException">The built prompt fails the privacy guard.</exception>
    public static BallotPrompt Build(BallotPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Motion);
        ArgumentNullException.ThrowIfNull(request.OtherVoterIds);

        var rules = request.Rules ?? BallotPromptRules.Defaults;
        rules.Validate();
        ValidateMotion(request.Motion);
        ValidateVoter(request);

        var choices = ChoicesFor(request.Motion);
        var reasoningTexts = new List<string>();

        var text = request.Phase == BallotPhase.Independent
            ? BuildIndependent(request.Motion, choices, rules)
            : BuildFollowUp(request, choices, rules, reasoningTexts);

        var result = PrivacyGuard.Check(new PrivacyCheckRequest(
            text,
            request.Phase,
            request.Context,
            Revealed: request.Phase == BallotPhase.FollowUp,
            request.OtherVoterIds,
            reasoningTexts));
        PrivacyGuard.EnsureAllowed(result);

        return new BallotPrompt(text, request.Phase, request.Context, request.TaintedAtWrite);
    }

    private static string BuildIndependent(CouncilMotion motion, IReadOnlyList<string> choices, BallotPromptRules rules)
    {
        var sb = new StringBuilder();
        AppendMotion(sb, motion, choices);

        sb.AppendLine();
        sb.AppendLine("This is your sealed independent ballot. You cannot see other voters' ballots or reasons yet.");
        sb.AppendLine("Write your choice and your reasoning.");
        AppendReplyForm(sb, rules);
        return sb.ToString();
    }

    private static string BuildFollowUp(
        BallotPromptRequest request,
        IReadOnlyList<string> choices,
        BallotPromptRules rules,
        List<string> reasoningTexts)
    {
        var own = request.OwnIndependentBallot!;
        var revealed = request.Revealed!;

        var sb = new StringBuilder();
        AppendMotion(sb, request.Motion, choices);

        sb.AppendLine();
        sb.AppendLine("Your independent ballot:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Choice: {own.Choice}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Reasoning: {own.Reasoning}");
        reasoningTexts.Add(own.Reasoning);

        sb.AppendLine();
        sb.AppendLine("Reasons from the eligible voters. Labels are anonymous and the order is random; the order says nothing about rank:");
        foreach (var reason in revealed)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{reason.Label}: {reason.Reasoning}");
            reasoningTexts.Add(reason.Reasoning);
        }

        sb.AppendLine();
        sb.AppendLine("You may change your choice after reading the reasons. Keeping your independent choice is allowed.");
        sb.AppendLine("Your reply replaces your independent choice only if you reply with a choice.");
        AppendReplyForm(sb, rules);
        return sb.ToString();
    }

    private static void AppendMotion(StringBuilder sb, CouncilMotion motion, IReadOnlyList<string> choices)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"Council vote: {motion.Subject}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Ballot type: {DescribeKind(motion)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Motion class: {DescribeClass(motion.MotionClass)}");
        sb.AppendLine();
        sb.AppendLine("Choices:");
        foreach (var choice in choices)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {choice}");
        }
    }

    private static void AppendReplyForm(StringBuilder sb, BallotPromptRules rules)
    {
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Reasoning must be at least {rules.MinSentences} sentences and at most {rules.MaxChars} characters.");
        sb.AppendLine();
        sb.AppendLine("Reply in this form:");
        sb.AppendLine("Choice: <one choice from the list>");
        sb.AppendLine("Reasoning: <your reasoning>");
    }

    private static string DescribeKind(CouncilMotion motion) =>
        motion.Kind == VoteKind.ApproveReject
            ? _approveRejectDescription
            : "pick one option from the list.";

    private static string DescribeClass(MotionClass motionClass) =>
        motionClass == MotionClass.BiggerChange
            ? "bigger change; it passes without the owner only if every eligible voter makes the same choice."
            : "ordinary; decided by a majority of the non-abstaining ballots.";

    private static IReadOnlyList<string> ChoicesFor(CouncilMotion motion) =>
        motion.Kind == VoteKind.ApproveReject
            ? [ApprovalChoices.Yes, ApprovalChoices.No, ApprovalChoices.Abstain]
            : motion.Options;

    private static void ValidateMotion(CouncilMotion motion)
    {
        if (string.IsNullOrWhiteSpace(motion.Subject))
        {
            throw new ArgumentException("Motion subject must not be blank.", nameof(motion));
        }

        ArgumentNullException.ThrowIfNull(motion.Options);

        if (motion.Kind == VoteKind.ApproveReject)
        {
            if (motion.Options.Count != 0)
            {
                throw new ArgumentException("Approve/reject motions take no options.", nameof(motion));
            }

            return;
        }

        if (motion.Options.Count is < Tally.MinOptions or > Tally.MaxOptions)
        {
            throw new ArgumentException(
                $"Option choice motions need {Tally.MinOptions} to {Tally.MaxOptions} options.", nameof(motion));
        }

        if (motion.Options.Any(string.IsNullOrWhiteSpace) || motion.Options.Distinct(StringComparer.Ordinal).Count() != motion.Options.Count)
        {
            throw new ArgumentException("Options must be non-blank and distinct.", nameof(motion));
        }
    }

    private static void ValidateVoter(BallotPromptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VoterId))
        {
            throw new ArgumentException("Voter id must not be blank.", nameof(request));
        }

        if (request.Phase == BallotPhase.Independent)
        {
            if (request.OwnIndependentBallot is not null)
            {
                throw new ArgumentException("An independent prompt cannot carry a ballot.", nameof(request));
            }

            if (request.Revealed is { Count: > 0 })
            {
                throw new ArgumentException("An independent prompt cannot carry revealed reasons.", nameof(request));
            }

            return;
        }

        var own = request.OwnIndependentBallot
            ?? throw new ArgumentException("A follow-up prompt needs the voter's own independent ballot.", nameof(request));
        if (own.Phase != BallotPhase.Independent || own.VoterId != request.VoterId)
        {
            throw new ArgumentException("The own ballot must be this voter's independent ballot.", nameof(request));
        }

        ValidateChoice(request.Motion, own.Choice);

        var revealed = request.Revealed
            ?? throw new ArgumentException("A follow-up prompt needs the revealed reasons.", nameof(request));
        if (revealed.Any(reason => string.IsNullOrWhiteSpace(reason.Label)) ||
            revealed.Select(reason => reason.Label).Distinct(StringComparer.Ordinal).Count() != revealed.Count)
        {
            throw new ArgumentException("Revealed reason labels must be non-blank and distinct.", nameof(request));
        }
    }

    private static void ValidateChoice(CouncilMotion motion, string choice)
    {
        if (!ChoicesFor(motion).Contains(choice, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Choice '{choice}' is not valid for this motion.", nameof(motion));
        }
    }
}
