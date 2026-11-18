using System.Text;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Modal;

/// <summary>
/// Pure modal state machine for a Calcinato terminal (doc 52, section 5.2). It turns key presses into effects the host
/// carries out: bytes for the shell, mode and status changes, scrolling, ex commands and yanks. It has no UI types and
/// reads no clock, so every transition can be tested with a fake <see cref="IScreen"/>.
/// </summary>
/// <remarks>
/// Positions are absolute rows (scrollback first, then the visible rows) and 0-based columns. The engine is not
/// thread-safe: the host calls <see cref="Handle"/> and <see cref="RefreshSuggestionAsync"/> from one thread.
/// </remarks>
public sealed partial class ModalEngine
{
    /// <summary>The built-in mnemonic that sends Ctrl-C (0x03) to the shell.</summary>
    private const string InterruptMnemonic = "intr";

    private const byte InterruptByte = 0x03;
    private const byte EndOfFileByte = 0x04;
    private const byte SuspendByte = 0x1A;
    private const int MaxCount = 99_999;
    private const int MaxCommandLength = 4096;
    private const int HistoryLimit = 200;
    private const string ReadOnlyMessage = "Read-only session: input is not sent.";

    private readonly IScreen _screen;
    private readonly IMnemonicBridge? _mnemonics;
    private readonly ISuggestionSource? _suggestions;
    private readonly bool _jkEscape;
    private readonly Func<bool> _applicationCursorKeys;
    private readonly ModalKeymap _keymap;
    private readonly List<string> _history = new();
    private readonly Dictionary<char, (int Row, int Col)> _marks = new();
    private readonly StringBuilder _inputLine = new();
    private readonly StringBuilder _typedSequence = new();

    private int _lastScrollback;
    private int _count;
    private PendingPrefix _pending;
    private int _pendingCount;
    private int _cursorRow;
    private int _cursorCol;
    private int _anchorRow;
    private int _anchorCol;
    private int _top;
    private string? _ghost;
    private bool _heldJ;

    private ModalMode _returnMode = ModalMode.Normal;
    private char _commandPrompt = ':';
    private string _commandText = string.Empty;
    private int _commandCursor;
    private int _historyIndex;
    private string _draft = string.Empty;
    private string? _lastPattern;
    private bool _lastSearchBackward;

    /// <summary>Creates an engine on a screen. Starts in Normal mode with the navigation cursor on the live cursor.</summary>
    /// <param name="screen">The screen the engine reads.</param>
    /// <param name="mnemonics">Hands typed sequences to the client-wide mnemonic router. Null means no mnemonics.</param>
    /// <param name="suggestions">Ghost text source. Null returns nothing.</param>
    /// <param name="jkEscape">When true, typing <c>j</c> then <c>k</c> in Insert mode leaves Insert mode. Off by default.</param>
    /// <param name="applicationCursorKeys">Reports whether the shell has enabled application cursor keys (DECCKM). Null means off.</param>
    /// <param name="keymap">Normal-mode bindings. Null uses <see cref="ModalKeymap.CreateDefault"/>.</param>
    public ModalEngine(
        IScreen screen,
        IMnemonicBridge? mnemonics = null,
        ISuggestionSource? suggestions = null,
        bool jkEscape = false,
        Func<bool>? applicationCursorKeys = null,
        ModalKeymap? keymap = null)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _mnemonics = mnemonics;
        _suggestions = suggestions;
        _jkEscape = jkEscape;
        _applicationCursorKeys = applicationCursorKeys ?? (() => false);
        _keymap = keymap ?? ModalKeymap.CreateDefault();
        _lastScrollback = screen.ScrollbackCount;
        SyncToLive();
        _anchorRow = _cursorRow;
        _anchorCol = _cursorCol;
    }

    /// <summary>The current mode.</summary>
    public ModalMode Mode { get; private set; } = ModalMode.Normal;

    /// <summary>False for a read-only viewer. Insert mode and shell-bound commands are refused then. Normal mode still works.</summary>
    public bool CanWrite { get; set; } = true;

    /// <summary>Navigation cursor row, absolute.</summary>
    public int CursorRow => _cursorRow;

    /// <summary>Navigation cursor column, 0-based.</summary>
    public int CursorColumn => _cursorCol;

    /// <summary>First visible absolute row.</summary>
    public int TopRow => _top;

    /// <summary>The ghost text currently offered, or null.</summary>
    public string? GhostSuggestion => _ghost;

    /// <summary>The prompt of the command line: <c>:</c>, <c>/</c> or <c>?</c>. Meaningful in CommandLine mode.</summary>
    public char CommandPrompt => _commandPrompt;

    /// <summary>The text typed on the command line so far.</summary>
    public string CommandText => _commandText;

    /// <summary>The caret position inside <see cref="CommandText"/>.</summary>
    public int CommandCursor => _commandCursor;

    /// <summary>Handles one key press and returns what the host must do.</summary>
    /// <param name="key">The key press.</param>
    public ModalResult Handle(TerminalKey key)
    {
        var fx = new List<ModalEffect>();
        int beforeRow = _cursorRow;
        int beforeCol = _cursorCol;
        int beforeTop = _top;

        SyncScrollback();
        if (Mode == ModalMode.Insert && !CanWrite)
        {
            SetMode(fx, ModalMode.Normal);
            Status(fx, "Read-only session: Insert mode is not available.");
        }

        Dispatch(key, fx);
        if (Mode == ModalMode.Insert)
        {
            SyncToLive();
        }

        KeepCursorVisible();
        EmitPosition(fx, beforeRow, beforeCol, beforeTop);
        return new ModalResult(fx.ToArray());
    }

    /// <summary>
    /// Asks the suggestion source for ghost text for what has been typed in Insert mode. Stale answers (the typed line
    /// changed while waiting) are dropped. Nothing is sent to the shell until the person accepts with Tab.
    /// </summary>
    public async ValueTask<ModalResult> RefreshSuggestionAsync(CancellationToken ct)
    {
        var fx = new List<ModalEffect>();
        if (_suggestions is null || Mode != ModalMode.Insert || !CanWrite || _inputLine.Length == 0)
        {
            ClearGhost(fx);
            return new ModalResult(fx.ToArray());
        }

        string line = _inputLine.ToString();
        string? suggestion = await _suggestions.SuggestAsync(line, ct).ConfigureAwait(false);
        if (Mode != ModalMode.Insert || _inputLine.ToString() != line)
        {
            return ModalResult.None;
        }

        if (string.IsNullOrEmpty(suggestion))
        {
            ClearGhost(fx);
        }
        else
        {
            _ghost = suggestion;
            fx.Add(new ModalEffect.GhostText(suggestion));
        }

        return new ModalResult(fx.ToArray());
    }

    private enum PendingPrefix
    {
        None,
        GPrefix,
        SetMark,
        JumpMark,
    }

    private int TotalRows => _screen.ScrollbackCount + _screen.Rows;

    private int PageSize => Math.Max(1, _screen.Rows);

    private string LineText(int row) => _screen.GetLineText(row) ?? string.Empty;

    private void Dispatch(TerminalKey key, List<ModalEffect> fx)
    {
        if (key.Kind == TerminalKeyKind.Text && key.Text.Length == 0)
        {
            return;
        }

        if (key.Kind == TerminalKeyKind.Text && key.Text.Length > 1 && Mode != ModalMode.Insert)
        {
            foreach (char c in key.Text)
            {
                Dispatch(TerminalKey.Typed(c), fx);
            }

            return;
        }

        switch (Mode)
        {
            case ModalMode.Insert:
                HandleInsert(key, fx);
                break;
            case ModalMode.CommandLine:
                HandleCommandLine(key, fx);
                break;
            default:
                HandleNormalOrVisual(key, fx);
                break;
        }
    }

    private void HandleInsert(TerminalKey key, List<ModalEffect> fx)
    {
        if (_heldJ)
        {
            _heldJ = false;
            if (key.Kind == TerminalKeyKind.Text && key.Text == "k")
            {
                ClearGhost(fx);
                SetMode(fx, ModalMode.Normal);
                SyncToLive();
                return;
            }

            SendText(fx, "j");
        }

        switch (key.Kind)
        {
            case TerminalKeyKind.Text:
                if (_jkEscape && key.Text == "j")
                {
                    _heldJ = true;
                    return;
                }

                ClearGhost(fx);
                _inputLine.Append(key.Text);
                SendText(fx, key.Text);
                return;

            case TerminalKeyKind.RawControl:
                // The shell owns the line after a control byte (Ctrl-C, Ctrl-U and so on), so the typed copy is no longer valid.
                ClearGhost(fx);
                _inputLine.Clear();
                fx.Add(new ModalEffect.SendBytes(new[] { key.ControlByte }));
                return;

            case TerminalKeyKind.Named:
                HandleInsertNamed(key, fx);
                return;
        }
    }

    private void HandleInsertNamed(TerminalKey key, List<ModalEffect> fx)
    {
        switch (key.Name)
        {
            case KeyName.Escape:
                ClearGhost(fx);
                SetMode(fx, ModalMode.Normal);
                SyncToLive();
                return;

            case KeyName.Tab when _ghost is not null && !key.Shift:
                string accepted = _ghost;
                ClearGhost(fx);
                _inputLine.Append(accepted);
                SendText(fx, accepted);
                return;

            case KeyName.Enter:
                _inputLine.Clear();
                break;

            case KeyName.Backspace:
                if (_inputLine.Length > 0)
                {
                    _inputLine.Length--;
                }

                break;

            case KeyName.Tab:
                break;

            default:
                _inputLine.Clear();
                break;
        }

        ClearGhost(fx);
        fx.Add(new ModalEffect.SendBytes(TerminalKeyEncoder.Encode(key, _applicationCursorKeys())));
    }

    private static void SendText(List<ModalEffect> fx, string text) =>
        fx.Add(new ModalEffect.SendBytes(Encoding.UTF8.GetBytes(text)));

    private void ClearGhost(List<ModalEffect> fx)
    {
        if (_ghost is not null)
        {
            _ghost = null;
            fx.Add(new ModalEffect.GhostText(null));
        }
    }

    private void SetMode(List<ModalEffect> fx, ModalMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        if (mode != ModalMode.Insert)
        {
            ClearGhost(fx);
        }

        fx.Add(new ModalEffect.ModeChanged(mode));
        fx.Add(new ModalEffect.Speech(ModeName(mode)));
    }

    private static string ModeName(ModalMode mode) => mode switch
    {
        ModalMode.Normal => "Normal mode",
        ModalMode.Insert => "Insert mode",
        ModalMode.VisualChar => "Visual mode, characters",
        ModalMode.VisualLine => "Visual mode, lines",
        ModalMode.CommandLine => "Command line",
        _ => "Unknown mode",
    };

    private static void Status(List<ModalEffect> fx, string text)
    {
        fx.Add(new ModalEffect.StatusLine(text));
        fx.Add(new ModalEffect.Speech(text));
    }

    private void SendToShell(List<ModalEffect> fx, byte[] bytes, string speech)
    {
        if (!CanWrite)
        {
            Status(fx, ReadOnlyMessage);
            return;
        }

        fx.Add(new ModalEffect.SendBytes(bytes));
        Status(fx, speech);
    }

    private void RequestInsert(List<ModalEffect> fx)
    {
        if (Mode is ModalMode.VisualChar or ModalMode.VisualLine)
        {
            Status(fx, "Insert is not available in Visual mode. Press Escape first.");
            return;
        }

        if (!CanWrite)
        {
            Status(fx, "Read-only session: Insert mode is not available.");
            return;
        }

        SetMode(fx, ModalMode.Insert);
        SyncToLive();
    }

    // Live position: the shell cursor on the visible area, with the view on the bottom.
    private void SyncToLive()
    {
        _top = _screen.ScrollbackCount;
        _cursorRow = _screen.ScrollbackCount + _screen.CursorRow;
        _cursorCol = _screen.CursorCol;
    }

    // Output that scrolls the screen adds scrollback lines. Shift absolute positions by the same amount so the
    // navigation cursor stays on the same text. A capped scrollback that drops lines cannot be seen here (doc 52 gap).
    private void SyncScrollback()
    {
        int scrollback = _screen.ScrollbackCount;
        int delta = scrollback - _lastScrollback;
        if (delta == 0)
        {
            return;
        }

        _lastScrollback = scrollback;
        _cursorRow = Math.Max(0, _cursorRow + delta);
        _anchorRow = Math.Max(0, _anchorRow + delta);
        _top = Math.Max(0, _top + delta);
        foreach (char key in _marks.Keys.ToArray())
        {
            var mark = _marks[key];
            _marks[key] = (Math.Max(0, mark.Row + delta), mark.Col);
        }
    }

    private void KeepCursorVisible()
    {
        int total = TotalRows;
        _cursorRow = Math.Clamp(_cursorRow, 0, total - 1);
        _cursorCol = Math.Clamp(_cursorCol, 0, Math.Max(0, _screen.Cols - 1));
        if (_cursorRow < _top)
        {
            _top = _cursorRow;
        }
        else if (_cursorRow > _top + PageSize - 1)
        {
            _top = _cursorRow - PageSize + 1;
        }

        _top = Math.Clamp(_top, 0, Math.Max(0, total - PageSize));
    }

    private void EmitPosition(List<ModalEffect> fx, int beforeRow, int beforeCol, int beforeTop)
    {
        if (_cursorRow != beforeRow || _cursorCol != beforeCol)
        {
            fx.Add(new ModalEffect.CursorMoved(_cursorRow, _cursorCol));
        }

        if (_top != beforeTop)
        {
            fx.Add(new ModalEffect.ScrollTo(_top));
        }
    }
}
