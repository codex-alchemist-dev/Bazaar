namespace Bazaar.Client.Calcinato.Abstractions;

/// <summary>
/// Read-only view of a terminal screen plus its scrollback, as the modal engine and the pane view model need it.
/// Rows are addressed from the oldest scrollback line (0) to the last visible row; <see cref="ScrollbackCount"/> lines
/// come first, then <see cref="Rows"/> visible rows.
/// </summary>
public interface IScreen
{
    /// <summary>Visible columns.</summary>
    int Cols { get; }

    /// <summary>Visible rows.</summary>
    int Rows { get; }

    /// <summary>Lines kept above the visible area (always 0 on the alternate screen).</summary>
    int ScrollbackCount { get; }

    /// <summary>True while the alternate screen is active.</summary>
    bool IsAlternateScreen { get; }

    /// <summary>Cursor row within the visible area, 0-based.</summary>
    int CursorRow { get; }

    /// <summary>Cursor column, 0-based.</summary>
    int CursorCol { get; }

    /// <summary>Window title, already sanitised.</summary>
    string Title { get; }

    /// <summary>Text of one line (scrollback plus visible), trailing blanks trimmed. Out of range returns an empty string.</summary>
    string GetLineText(int absoluteRow);
}
