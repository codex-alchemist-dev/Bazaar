namespace Bazaar.Client.Calcinato.Modal;

/// <summary>The modes of the modal engine. Visual exists in two kinds, character-wise and line-wise.</summary>
public enum ModalMode
{
    /// <summary>Navigation and command entry.</summary>
    Normal,

    /// <summary>Keys go to the shell (or are refused on a read-only session).</summary>
    Insert,

    /// <summary>Visual selection by character.</summary>
    VisualChar,

    /// <summary>Visual selection by line.</summary>
    VisualLine,

    /// <summary>Ex line (for <c>:</c>) or a search prompt (for <c>/</c> and <c>?</c>).</summary>
    CommandLine,
}

/// <summary>
/// Something the host must do after a key press. The engine never performs I/O itself (doc 52, section 5.2).
/// </summary>
public abstract record ModalEffect
{
    /// <summary>Write these bytes to the terminal source.</summary>
    public sealed record SendBytes(byte[] Bytes) : ModalEffect;

    /// <summary>The mode changed.</summary>
    public sealed record ModeChanged(ModalMode Mode) : ModalEffect;

    /// <summary>Text for the status line (also used for command results and errors).</summary>
    public sealed record StatusLine(string Text) : ModalEffect;

    /// <summary>Text for the screen reader live region (doc 49).</summary>
    public sealed record Speech(string Text) : ModalEffect;

    /// <summary>An ex command the host carries out. The command is in <c>Request</c>.</summary>
    public sealed record Command(ExCommand Request) : ModalEffect;

    /// <summary>The first visible absolute row changed (scrollback plus screen addressing).</summary>
    public sealed record ScrollTo(int TopRow) : ModalEffect;

    /// <summary>The navigation cursor moved (absolute row, 0-based column).</summary>
    public sealed record CursorMoved(int Row, int Column) : ModalEffect;

    /// <summary>Text to copy to the client clipboard (from a Visual yank).</summary>
    public sealed record Yank(string Text) : ModalEffect;

    /// <summary>Ghost text to show after the typed input, or null to clear it. Accepting it sends it to the shell.</summary>
    public sealed record GhostText(string? Text) : ModalEffect;
}

/// <summary>The effects produced by one key press, in order.</summary>
/// <param name="Effects">The effects, in the order the host should apply them.</param>
public sealed record ModalResult(IReadOnlyList<ModalEffect> Effects)
{
    /// <summary>A key press with no effect.</summary>
    public static ModalResult None { get; } = new(Array.Empty<ModalEffect>());
}
