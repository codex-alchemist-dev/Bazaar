using Bazaar.Client.Core.Input;

namespace Bazaar.Ex;

/// <summary>What one key press did, in the terms a head needs: the outcome, an action or command to carry out, and the speech and overlay text.</summary>
public sealed record MnemonicStep(
    RouteOutcome Outcome,
    RouterAction? Action,
    string? CommandId,
    string? Speech,
    AnnouncePriority Priority,
    string? Overlay);

/// <summary>
/// The mnemonic state machine for one client, joined to its resolved assignments. Pure: a head feeds it key
/// strokes and carries out the returned step. No UI types, so every rule here is testable without Avalonia.
/// </summary>
public sealed class MnemonicSession
{
    private readonly KeyRouter _router;
    private readonly IReadOnlyList<MnemonicAssignment> _effective;

    public MnemonicSession(MnemonicTable table, IReadOnlyList<MnemonicAssignment> effective)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(effective);
        _effective = effective;
        _router = new KeyRouter(table);
    }

    public InputMode Mode => _router.Mode;

    /// <summary>Builds a session from a resolution, or returns null when the resolution has errors. The palette key is validated here too.</summary>
    public static MnemonicSession? TryCreate(MnemonicResolution resolution, IEnumerable<KeyStroke> paletteKeys)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var table = resolution.BuildTable(paletteKeys);
        return table is null ? null : new MnemonicSession(table, resolution.Effective);
    }

    public MnemonicStep Handle(KeyStroke key, RouterContext context)
    {
        var result = _router.Handle(key, context);
        var priority = result.Outcome is RouteOutcome.NoMatch or RouteOutcome.NoTarget
            ? AnnouncePriority.Assertive
            : AnnouncePriority.Polite;
        return new MnemonicStep(
            result.Outcome,
            result.Action,
            result.CommandId,
            MnemonicFeedback.For(result, _effective),
            priority,
            OverlayFor(result.Chord));
    }

    /// <summary>The expansion the overlay shows before Enter runs it. Null when nothing is typed or nothing matches yet.</summary>
    private string? OverlayFor(ChordState chord)
    {
        if (chord.IsEmpty)
        {
            return null;
        }

        var intent = MnemonicGrammar.Parse(chord.Typed, _effective);
        return intent.Binding is null ? null : intent.Explanation;
    }
}
