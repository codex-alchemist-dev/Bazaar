using Bazaar.Client.Core.Input;

namespace Bazaar.Ex;

/// <summary>Priority of a screen-reader announcement. Polite waits its turn; assertive interrupts.</summary>
public enum AnnouncePriority
{
    Polite,
    Assertive,
}

/// <summary>Carries announcements to the platform's screen reader. Heads implement it with their accessibility API.</summary>
public interface IMnemonicAnnouncer
{
    void Announce(string text, AnnouncePriority priority);
}

/// <summary>Discards announcements. Used when no screen reader is attached and in tests that do not check them.</summary>
public sealed class NullMnemonicAnnouncer : IMnemonicAnnouncer
{
    public static NullMnemonicAnnouncer Instance { get; } = new();

    public void Announce(string text, AnnouncePriority priority)
    {
    }
}

/// <summary>
/// Turns key-router outcomes into the words a screen reader speaks. Pure: the head decides when to call it.
/// </summary>
public static class MnemonicFeedback
{
    /// <summary>Limit on how many hints are read out, so a short prefix does not flood the reader.</summary>
    public const int MaxSpokenHints = 5;

    /// <summary>The text for one router result, or null when the result needs no speech.</summary>
    public static string? For(RouteResult result, IReadOnlyList<MnemonicAssignment> effective)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(effective);

        switch (result.Outcome)
        {
            case RouteOutcome.Pending:
                return PendingText(result.Chord, effective);
            case RouteOutcome.Executed:
                return ExecutedText(result.CommandId, effective);
            case RouteOutcome.Cancelled:
                return "Cancelled.";
            case RouteOutcome.NoMatch:
                return "No mnemonic matches.";
            case RouteOutcome.NoTarget:
                return "Nothing here takes text input.";
            default:
                return null;
        }
    }

    private static string PendingText(ChordState chord, IReadOnlyList<MnemonicAssignment> effective)
    {
        var candidates = MnemonicGrammar.Candidates(chord.Typed, effective);
        var spoken = candidates.Take(MaxSpokenHints).Select(c => $"{c.Mnemonic}, {c.Title}");
        var more = candidates.Count > MaxSpokenHints ? $" and {candidates.Count - MaxSpokenHints} more" : string.Empty;
        return $"{chord.Typed}. {string.Join("; ", spoken)}{more}. Press Enter to run.";
    }

    private static string? ExecutedText(string? commandId, IReadOnlyList<MnemonicAssignment> effective)
    {
        var match = effective.FirstOrDefault(a => a.CommandId == commandId);
        return match is null ? null : $"{match.Title}.";
    }
}
