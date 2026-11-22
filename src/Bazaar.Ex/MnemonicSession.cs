using Bazaar.Client.Input;

namespace Bazaar.Ex;

/// <summary>What one key press did, in the terms a head needs: the outcome, an action or command to carry out, and the speech and overlay text.</summary>
public sealed record MnemonicStep(
    RouteOutcome Outcome,
    RouterAction? Action,
    string? CommandId,
    string? Speech,
    AnnouncePriority Priority,
    string? Overlay,
    string? AiSuggestFor = null);

/// <summary>
/// The mnemonic state machine for one client, joined to its resolved assignments. Pure: a head feeds it key
/// strokes and carries out the returned step. No UI types, so every rule here is testable without Avalonia.
/// </summary>
public sealed class MnemonicSession
{
    private readonly KeyRouter _router;
    private readonly IReadOnlyList<MnemonicAssignment> _effective;
    private readonly MnemonicOptions _options;
    private string _typed = string.Empty;

    public MnemonicSession(MnemonicTable table, IReadOnlyList<MnemonicAssignment> effective, MnemonicOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(effective);
        _effective = effective;
        _options = options ?? MnemonicOptions.Default;
        _router = new KeyRouter(table);
    }

    public InputMode Mode => _router.Mode;

    /// <summary>Builds a session from a resolution, or returns null when the resolution has errors. The palette key is validated here too.</summary>
    public static MnemonicSession? TryCreate(MnemonicResolution resolution, IEnumerable<KeyStroke> paletteKeys, MnemonicOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var table = resolution.BuildTable(paletteKeys);
        return table is null ? null : new MnemonicSession(table, resolution.Effective, options);
    }

    /// <summary>The text typed so far toward a mnemonic. Empty when nothing is pending.</summary>
    public string Typed => _typed;

    public MnemonicStep Handle(KeyStroke key, RouterContext context)
    {
        if (_options.AiSuggestEnabled
            && key.Named == NamedKey.Enter
            && (key.Modifiers & KeyModifiers.Shift) != 0
            && _typed.Length > 0)
        {
            // Shift+Enter asks for suggestions only. The router never sees it, so nothing runs (doc 41, section 4.12).
            return new MnemonicStep(
                RouteOutcome.Pending,
                null,
                null,
                $"Asking for suggestions for '{_typed}'.",
                AnnouncePriority.Polite,
                OverlayFor(_typed),
                _typed);
        }

        var result = _router.Handle(key, context);
        _typed = result.Chord.Typed;
        var priority = result.Outcome is RouteOutcome.NoMatch or RouteOutcome.NoTarget
            ? AnnouncePriority.Assertive
            : AnnouncePriority.Polite;
        return new MnemonicStep(
            result.Outcome,
            result.Action,
            result.CommandId,
            MnemonicFeedback.For(result, _effective),
            priority,
            OverlayFor(result.Chord.Typed));
    }

    /// <summary>The expansion the overlay shows before Enter runs it. Null when nothing is typed or nothing matches yet.</summary>
    private string? OverlayFor(string typed)
    {
        if (typed.Length == 0)
        {
            return null;
        }

        var intent = MnemonicGrammar.Parse(typed, _effective, _options);
        return intent.Binding is null ? null : intent.Explanation;
    }
}
