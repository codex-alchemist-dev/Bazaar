namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// Reasoning length guidance written into ballot instructions (council.reason_min_sentences, council.reason_max_chars;
/// docs/design/10-council-and-voting.md, Settings). The prompt states the rules; it does not enforce them.
/// </summary>
/// <param name="MinSentences">Council default 2. At least 1.</param>
/// <param name="MaxChars">Council default 2000. At least 1.</param>
public sealed record BallotPromptRules(int MinSentences = 2, int MaxChars = 2000)
{
    /// <summary>The shipped defaults.</summary>
    public static BallotPromptRules Defaults { get; } = new();

    /// <summary>Checks the bounds. Called by the builder.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is below 1.</exception>
    public void Validate()
    {
        if (MinSentences < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSentences), MinSentences, "Minimum sentences must be at least 1.");
        }

        if (MaxChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChars), MaxChars, "Maximum characters must be at least 1.");
        }
    }
}
