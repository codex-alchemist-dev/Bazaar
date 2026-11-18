using Bazaar.Client.Calcinato.Emulation;

namespace Bazaar.Client.Calcinato.Panes;

/// <summary>One run of adjacent cells that share a style.</summary>
/// <param name="Text">The cell text, with one character per column (wide characters take their cell).</param>
/// <param name="Style">The style shared by the cells.</param>
public sealed record TerminalCellRun(string Text, CellStyle Style);

/// <summary>One visible row as styled runs, in column order.</summary>
/// <param name="Runs">The runs that cover the row.</param>
public sealed record TerminalRow(IReadOnlyList<TerminalCellRun> Runs)
{
    /// <summary>The row text with trailing blanks removed.</summary>
    public string Text => string.Concat(Runs.Select(run => run.Text)).TrimEnd();
}

/// <summary>
/// The line-oriented text model for screen readers (doc 55, section 5.2): the visible rows and the cursor row.
/// </summary>
/// <param name="VisibleRows">Visible rows, trailing blanks removed.</param>
/// <param name="CursorRow">Index of the cursor row in <paramref name="VisibleRows"/>, or -1 when it is off screen.</param>
/// <param name="CursorRowText">Text of the cursor row, even when it is off screen.</param>
public sealed record TerminalAccessibleText(IReadOnlyList<string> VisibleRows, int CursorRow, string CursorRowText)
{
    /// <summary>The text before any session exists.</summary>
    public static TerminalAccessibleText Empty { get; } = new([], -1, string.Empty);
}

/// <summary>Everything a terminal pane draws, captured in one immutable value.</summary>
/// <param name="HasSession">False when no terminal is open.</param>
/// <param name="Rows">Visible rows (the navigation view, which follows the live screen in Insert mode).</param>
/// <param name="CursorRow">Navigation cursor row index in <paramref name="Rows"/>, or -1 when off screen.</param>
/// <param name="CursorColumn">Navigation cursor column, 0-based.</param>
/// <param name="LiveCursorRow">The program's cursor row in <paramref name="Rows"/>, or -1 when it is off screen.</param>
/// <param name="LiveCursorColumn">The program's cursor column, 0-based.</param>
/// <param name="CursorVisible">DECTCEM state from the program.</param>
/// <param name="ModeName">NORMAL, INSERT, VISUAL, V-LINE or COMMAND.</param>
/// <param name="CommandLine">The ex or search line with its prompt, in Command mode; empty otherwise.</param>
/// <param name="StatusLine">The latest status text.</param>
/// <param name="Title">The window title the program set (OSC 0 or 2), sanitised.</param>
/// <param name="IsReadOnly">True for a viewer without the writer role.</param>
/// <param name="IsReplaying">True while output from the scrollback ring is still arriving.</param>
/// <param name="ExitCode">The exit code once the terminal ended, or null.</param>
/// <param name="SelectionAnchorRow">Visual selection anchor row relative to <paramref name="Rows"/>; may lie off screen. Meaningful in the Visual modes.</param>
/// <param name="SelectionAnchorColumn">Visual selection anchor column, 0-based.</param>
public sealed record TerminalPaneSnapshot(
    bool HasSession,
    IReadOnlyList<TerminalRow> Rows,
    int CursorRow,
    int CursorColumn,
    int LiveCursorRow,
    int LiveCursorColumn,
    bool CursorVisible,
    string ModeName,
    string CommandLine,
    string StatusLine,
    string Title,
    bool IsReadOnly,
    bool IsReplaying,
    int? ExitCode,
    int SelectionAnchorRow = -1,
    int SelectionAnchorColumn = 0)
{
    /// <summary>Status shown when no terminal is open.</summary>
    public const string NoSessionStatus = "No terminal is open. Use :new to start a shell.";

    /// <summary>The snapshot before any session exists.</summary>
    public static TerminalPaneSnapshot Empty { get; } = new(
        HasSession: false,
        Rows: [],
        CursorRow: -1,
        CursorColumn: 0,
        LiveCursorRow: -1,
        LiveCursorColumn: 0,
        CursorVisible: false,
        ModeName: "NORMAL",
        CommandLine: string.Empty,
        StatusLine: NoSessionStatus,
        Title: string.Empty,
        IsReadOnly: false,
        IsReplaying: false,
        ExitCode: null,
        SelectionAnchorRow: -1,
        SelectionAnchorColumn: 0);
}
