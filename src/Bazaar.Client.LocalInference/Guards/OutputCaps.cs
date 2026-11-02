namespace Bazaar.Client.LocalInference.Guards;

/// <summary>
/// Output caps per task kind, in characters (doc 31, Design). Over-cap output is cut to the cap and flagged.
/// These are shipped defaults for the prompt templates; they are not user settings.
/// </summary>
public static class OutputCaps
{
    /// <summary>
    /// Maximum output characters per kind:
    /// ChipSummary 160 (one chip line), VoiceCleanup 2000 (about one dictation of a few minutes),
    /// PromptRewrite 4000 (a prompt the user reviews before sending), UiAssist 500 (short UI text).
    /// </summary>
    private static readonly Dictionary<LocalTaskKind, int> _maxChars = new()
    {
        [LocalTaskKind.ChipSummary] = 160,
        [LocalTaskKind.VoiceCleanup] = 2000,
        [LocalTaskKind.PromptRewrite] = 4000,
        [LocalTaskKind.UiAssist] = 500,
    };

    /// <summary>Output cap in characters for a task kind.</summary>
    public static int CapFor(LocalTaskKind kind) => _maxChars[kind];

    /// <summary>Cuts <paramref name="output"/> to the cap for <paramref name="kind"/> and reports whether it was cut.</summary>
    public static CappedText Apply(LocalTaskKind kind, string output)
    {
        var cap = CapFor(kind);
        if (output.Length <= cap)
        {
            return new CappedText(output, Truncated: false);
        }

        return new CappedText(output[..cap].TrimEnd(), Truncated: true);
    }
}
