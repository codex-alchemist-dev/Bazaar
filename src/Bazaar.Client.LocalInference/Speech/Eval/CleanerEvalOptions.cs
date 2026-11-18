namespace Bazaar.Client.LocalInference.Speech.Eval;

/// <summary>
/// Harness settings for the cleaner eval. These are test-harness choices, not operator settings (doc 29 leaves the scoring
/// unspecified; CLAUDE.md configurability rule). The defaults are the shipped values.
/// </summary>
public sealed record CleanerEvalOptions
{
    /// <summary>Filler phrases that must not survive cleanup. Matched as whole words or phrases, ignoring case.</summary>
    public IReadOnlyList<string> Fillers { get; init; } = ["um", "uh", "uhm", "erm", "er", "you know", "I mean"];

    /// <summary>
    /// When true, a case also passes only if the shown text equals the expected text after whitespace normalisation.
    /// Off by default: exact wording is one acceptable cleanup, so the criteria checks gate and the match rate is reported.
    /// </summary>
    public bool GateOnExactMatch { get; init; }

    /// <summary>Checks the options. Throws <see cref="ArgumentException"/> when the list is empty or a filler is blank.</summary>
    public void Validate()
    {
        if (Fillers.Count == 0 || Fillers.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Fillers must hold at least one non-blank phrase.", nameof(Fillers));
        }
    }
}
