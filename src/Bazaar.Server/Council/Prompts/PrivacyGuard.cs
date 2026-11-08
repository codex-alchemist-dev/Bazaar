namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// Pure privacy check for ballot prompts (docs/design/10-council-and-voting.md, Private storage and reveal; Testing: Privacy).
/// No storage, clock or model calls. It can only catch text it is given, so callers pass every peer id and reasoning
/// text they hold. Matching is ordinal and case-sensitive; blank snippets are ignored because they cannot leak anything.
/// </summary>
public static class PrivacyGuard
{
    /// <summary>
    /// String-level check: denies when the prompt contains any non-blank forbidden snippet (peer ballot text or a peer id).
    /// </summary>
    public static PrivacyCheckResult EnsureNoPeerBallotLeak(string prompt, IEnumerable<string> forbiddenSnippets)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(forbiddenSnippets);

        foreach (var snippet in forbiddenSnippets)
        {
            if (!IsBlank(snippet) && prompt.Contains(snippet, StringComparison.Ordinal))
            {
                return new PrivacyCheckResult.Denied("Prompt contains forbidden peer ballot content.");
            }
        }

        return new PrivacyCheckResult.Allowed();
    }

    /// <summary>
    /// Typed check. Rules, in order:
    /// <list type="number">
    /// <item>A follow-up prompt before the reveal is denied.</item>
    /// <item>A Thinking prompt before the reveal that carries any known reasoning text is denied.</item>
    /// <item>A prompt that contains any peer voter id is denied (every phase).</item>
    /// <item>An independent prompt that contains any known reasoning text is denied.</item>
    /// </list>
    /// </summary>
    public static PrivacyCheckResult Check(PrivacyCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prompt);
        ArgumentNullException.ThrowIfNull(request.PeerVoterIds);
        ArgumentNullException.ThrowIfNull(request.ReasoningTexts);

        if (request.Phase == BallotPhase.FollowUp && !request.Revealed)
        {
            return new PrivacyCheckResult.Denied("Follow-up prompt built before the reveal.");
        }

        var carriesReasoning = ContainsAny(request.Prompt, request.ReasoningTexts);

        if (request.Context == PromptContext.Thinking && !request.Revealed && carriesReasoning)
        {
            return new PrivacyCheckResult.Denied("Thinking GC prompt carries vote reasoning before the reveal.");
        }

        if (ContainsAny(request.Prompt, request.PeerVoterIds))
        {
            return new PrivacyCheckResult.Denied("Prompt contains a peer voter id.");
        }

        if (request.Phase == BallotPhase.Independent && carriesReasoning)
        {
            return new PrivacyCheckResult.Denied("Independent prompt contains revealed or other reasoning text.");
        }

        return new PrivacyCheckResult.Allowed();
    }

    /// <summary>Throws <see cref="CouncilPrivacyViolationException"/> when the result is a denial.</summary>
    /// <exception cref="CouncilPrivacyViolationException">The result is <see cref="PrivacyCheckResult.Denied"/>.</exception>
    public static void EnsureAllowed(PrivacyCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result is PrivacyCheckResult.Denied denied)
        {
            throw new CouncilPrivacyViolationException(denied.Reason);
        }
    }

    private static bool ContainsAny(string prompt, IEnumerable<string> snippets) =>
        snippets.Any(snippet => !IsBlank(snippet) && prompt.Contains(snippet, StringComparison.Ordinal));

    private static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);
}
