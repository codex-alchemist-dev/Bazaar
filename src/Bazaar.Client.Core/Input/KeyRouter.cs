namespace Bazaar.Client.Core.Input;

public enum InputMode
{
    Normal,
    Insert,
    Palette,
}

public enum RouteOutcome
{
    /// <summary>Not handled here; the head delivers the key to the focused control or the OS.</summary>
    Passthrough,

    /// <summary>Handled here with no command. Check <see cref="RouteResult.Action"/>.</summary>
    Consumed,

    /// <summary>A mnemonic is being typed. <see cref="RouteResult.Chord"/> holds the hints.</summary>
    Pending,

    /// <summary>A mnemonic was executed (Enter). <see cref="RouteResult.CommandId"/> names it.</summary>
    Executed,

    /// <summary>Esc cancelled a pending mnemonic.</summary>
    Cancelled,

    /// <summary>The typed text matched nothing, or Enter found no exact mnemonic. Pending is cleared.</summary>
    NoMatch,

    /// <summary>'i' was pressed with nothing focusable to enter Insert on. Mode stays Normal.</summary>
    NoTarget,
}

public enum RouterActionKind
{
    FocusTextInput,
    ReleaseTextFocus,
    OpenPalette,
    ClosePalette,
    PaletteInput,
    PaletteBackspace,
    PaletteSelectNext,
    PaletteSelectPrevious,
    PaletteSubmit,
}

public sealed record RouterAction(RouterActionKind Kind, char? Character = null);

/// <summary>What the head knows about focus and composition at the time of the key press.</summary>
public sealed record RouterContext(bool TextInputFocused = false, bool HasFocusableTextInput = false, bool ImeComposing = false);

/// <summary>Typed text of the pending mnemonic and the bindings it can still become (the which-key hints).</summary>
public sealed record ChordState(string Typed, IReadOnlyList<MnemonicBinding> Hints)
{
    public static ChordState Empty { get; } = new(string.Empty, []);

    public bool IsEmpty => Typed.Length == 0;
}

public sealed record RouteResult(RouteOutcome Outcome, InputMode Mode, ChordState Chord, RouterAction? Action = null, string? CommandId = null);

/// <summary>
/// Key router and mode state machine. Pure: no timers, no UI types. The head calls
/// <see cref="Handle"/> for each key press and carries out the returned action.
/// </summary>
/// <remarks>
/// Modes: Normal (nothing focused; letters are commands), Insert (any focused text input),
/// Palette (fuzzy palette open). Mnemonics execute only on Enter. Esc cancels a pending
/// mnemonic, leaves Insert, or closes the palette. IME composition is never intercepted.
/// </remarks>
public sealed class KeyRouter
{
    private readonly MnemonicTable _table;
    private string _typed = string.Empty;

    public KeyRouter(MnemonicTable table)
    {
        _table = table;
    }

    public InputMode Mode { get; private set; } = InputMode.Normal;

    public ChordState Chord =>
        _typed.Length == 0 ? ChordState.Empty : new ChordState(_typed, _table.CandidatesFor(_typed));

    public RouteResult Handle(KeyStroke key, RouterContext context)
    {
        if (context.ImeComposing)
        {
            return Result(RouteOutcome.Passthrough);
        }

        SyncFocus(context);

        return Mode switch
        {
            InputMode.Insert => HandleInsert(key),
            InputMode.Palette => HandlePalette(key),
            _ => HandleNormal(key, context),
        };
    }

    private void SyncFocus(RouterContext context)
    {
        if (Mode == InputMode.Palette)
        {
            return;
        }

        if (context.TextInputFocused)
        {
            EnterMode(InputMode.Insert);
        }
        else if (Mode == InputMode.Insert)
        {
            Mode = InputMode.Normal;
        }
    }

    private RouteResult HandleInsert(KeyStroke key)
    {
        if (key.Named == NamedKey.Escape && !key.HasChordModifier)
        {
            Mode = InputMode.Normal;
            return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.ReleaseTextFocus));
        }

        return Result(RouteOutcome.Passthrough);
    }

    private RouteResult HandlePalette(KeyStroke key)
    {
        if (key.HasChordModifier)
        {
            return Result(RouteOutcome.Passthrough);
        }

        switch (key.Named)
        {
            case NamedKey.Escape:
                Mode = InputMode.Normal;
                return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.ClosePalette));
            case NamedKey.Enter:
                Mode = InputMode.Normal;
                return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.PaletteSubmit));
            case NamedKey.Backspace:
                return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.PaletteBackspace));
            case NamedKey.Up:
                return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.PaletteSelectPrevious));
            case NamedKey.Down:
                return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.PaletteSelectNext));
            case NamedKey.None when key.Character is char c:
                return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.PaletteInput, c));
            default:
                return Result(RouteOutcome.Passthrough);
        }
    }

    private RouteResult HandleNormal(KeyStroke key, RouterContext context)
    {
        if (key.HasChordModifier)
        {
            return Result(RouteOutcome.Passthrough);
        }

        switch (key.Named)
        {
            case NamedKey.Escape:
                if (_typed.Length == 0)
                {
                    return Result(RouteOutcome.Passthrough);
                }

                _typed = string.Empty;
                return Result(RouteOutcome.Cancelled);

            case NamedKey.Enter:
                return HandleEnter();

            case NamedKey.Backspace:
                if (_typed.Length == 0)
                {
                    return Result(RouteOutcome.Passthrough);
                }

                _typed = _typed[..^1];
                return Result(_typed.Length == 0 ? RouteOutcome.Cancelled : RouteOutcome.Pending);

            case NamedKey.None:
                return HandlePrintable(key.Character, context);

            default:
                // Any other named key ends a pending mnemonic and goes to the focused control.
                _typed = string.Empty;
                return Result(RouteOutcome.Passthrough);
        }
    }

    private RouteResult HandleEnter()
    {
        if (_typed.Length == 0)
        {
            return Result(RouteOutcome.Passthrough);
        }

        var typed = _typed;
        _typed = string.Empty;
        var binding = _table.Exact(typed);
        if (binding is null)
        {
            return Result(RouteOutcome.NoMatch);
        }

        if (binding.CommandId == MnemonicTable.PaletteCommandId)
        {
            Mode = InputMode.Palette;
            return Result(RouteOutcome.Executed, new RouterAction(RouterActionKind.OpenPalette), binding.CommandId);
        }

        return Result(RouteOutcome.Executed, null, binding.CommandId);
    }

    private RouteResult HandlePrintable(char? character, RouterContext context)
    {
        if (character is not char c)
        {
            return Result(RouteOutcome.Passthrough);
        }

        if (_typed.Length == 0 && c == MnemonicTable.ReservedInsertKey)
        {
            if (!context.HasFocusableTextInput)
            {
                return Result(RouteOutcome.NoTarget);
            }

            Mode = InputMode.Insert;
            return Result(RouteOutcome.Consumed, new RouterAction(RouterActionKind.FocusTextInput));
        }

        var next = _typed + c;
        if (_table.CandidatesFor(next).Count == 0)
        {
            _typed = string.Empty;
            return Result(RouteOutcome.NoMatch);
        }

        _typed = next;
        return Result(RouteOutcome.Pending);
    }

    private void EnterMode(InputMode mode)
    {
        Mode = mode;
        _typed = string.Empty;
    }

    private RouteResult Result(RouteOutcome outcome, RouterAction? action = null, string? commandId = null) =>
        new(outcome, Mode, Chord, action, commandId);
}
