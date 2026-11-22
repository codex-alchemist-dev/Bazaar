using System.Text;
using Bazaar.Client.Calcinato.Emulation;
using Bazaar.Client.Calcinato.Panes;

namespace Bazaar.Client.Terminal.Calcinato;

/// <summary>
/// Draws a <see cref="TerminalPaneSnapshot"/> into a real console as ANSI text. The pane takes every row but the last;
/// the last row is the status line (mode, status text or the command line). Only rows that changed since the previous
/// frame are written, SGR is written only when the style changes, and the cursor is placed last.
/// </summary>
/// <remarks>
/// Only printable runes from cell text reach the output. Control characters are dropped, so text from a shell can never
/// add a sequence of its own. The renderer keeps the previous frame, so it is not thread-safe; one loop calls it.
/// </remarks>
public sealed class AnsiScreenRenderer
{
    private const string _enterAlternateScreen = "\u001b[?1049h";
    private const string _leaveAlternateScreen = "\u001b[?1049l";
    private const string _hideCursor = "\u001b[?25l";
    private const string _showCursor = "\u001b[?25h";
    private const string _reset = "\u001b[0m";
    private const string _clearScreen = "\u001b[2J";
    private const string _home = "\u001b[H";
    private const string _statusMarkerReadOnly = " RO";

    private static readonly CellStyle _plain = CellStyle.Plain;
    private static readonly CellStyle _statusStyle = new(TermColor.Default, TermColor.Default, CellAttributes.Reverse);

    private GridCell[][]? _previous;
    private CellStyle _active = _plain;
    private bool _cursorShown;
    private CursorState? _lastCursor;

    /// <summary>
    /// Returns the sequence that switches to the alternate screen, hides the cursor and clears it. The next
    /// <see cref="Render"/> redraws every row.
    /// </summary>
    public string EnterScreen()
    {
        _previous = null;
        _active = _plain;
        _cursorShown = false;
        _lastCursor = null;
        return _enterAlternateScreen + _hideCursor + _reset + _clearScreen + _home;
    }

    /// <summary>Returns the sequence that restores the console: default style, cursor shown, primary screen.</summary>
    public string LeaveScreen()
    {
        _previous = null;
        _active = _plain;
        _cursorShown = false;
        _lastCursor = null;
        return _reset + _showCursor + _leaveAlternateScreen;
    }

    /// <summary>Forgets the previous frame, so the next <see cref="Render"/> redraws everything.</summary>
    public void Invalidate() => _previous = null;

    /// <summary>
    /// Returns the text that brings the console from the previous frame to <paramref name="snapshot"/>, or an empty
    /// string when nothing changed.
    /// </summary>
    /// <param name="snapshot">What to draw.</param>
    /// <param name="columns">Console columns. Wider content is clipped.</param>
    /// <param name="rows">Console rows. Taller content is clipped; the last row is the status line.</param>
    public string Render(TerminalPaneSnapshot snapshot, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (columns < 1 || rows < 1)
        {
            return string.Empty;
        }

        GridCell[][] grid = Build(snapshot, columns, rows, out CursorState? cursor);
        bool full = _previous is null || _previous.Length != rows || _previous[0].Length != columns;

        var output = new StringBuilder();
        if (full)
        {
            output.Append(_reset).Append(_clearScreen).Append(_home);
            _active = _plain;
            _cursorShown = false;
            _lastCursor = null;
        }

        bool wroteRows = false;
        for (int row = 0; row < rows; row++)
        {
            if (!full && _previous is not null && grid[row].AsSpan().SequenceEqual(_previous[row]))
            {
                continue;
            }

            WriteRow(output, row, grid[row]);
            wroteRows = true;
        }

        if (wroteRows && _active != _plain)
        {
            output.Append(_reset);
            _active = _plain;
        }

        if (wroteRows || cursor != _lastCursor)
        {
            WriteCursor(output, cursor);
            _lastCursor = cursor;
        }

        _previous = grid;
        return output.ToString();
    }

    private void WriteRow(StringBuilder output, int row, GridCell[] cells)
    {
        output.Append("\u001b[").Append(Invariant(row + 1)).Append(";1H");
        foreach (GridCell cell in cells)
        {
            if (cell.Text.Length == 0)
            {
                continue;
            }

            if (cell.Style != _active)
            {
                output.Append(Sgr(cell.Style));
                _active = cell.Style;
            }

            output.Append(cell.Text);
        }
    }

    private void WriteCursor(StringBuilder output, CursorState? cursor)
    {
        if (cursor is null)
        {
            if (_cursorShown)
            {
                output.Append(_hideCursor);
                _cursorShown = false;
            }

            return;
        }

        CursorState target = cursor.Value;
        output.Append("\u001b[").Append(Invariant(target.Row + 1)).Append(';').Append(Invariant(target.Column + 1)).Append('H');
        if (!_cursorShown)
        {
            output.Append(_showCursor);
            _cursorShown = true;
        }
    }

    private static GridCell[][] Build(TerminalPaneSnapshot snapshot, int columns, int rows, out CursorState? cursor)
    {
        var grid = new GridCell[rows][];
        for (int row = 0; row < rows; row++)
        {
            grid[row] = new GridCell[columns];
            Array.Fill(grid[row], new GridCell(" ", _plain));
        }

        int paneRows = rows - 1;
        for (int row = 0; row < paneRows && row < snapshot.Rows.Count; row++)
        {
            FillRow(grid[row], snapshot.Rows[row]);
        }

        int statusRow = rows - 1;
        bool inCommand = snapshot.CommandLine.Length > 0;
        string statusText = inCommand
            ? snapshot.CommandLine
            : $" {snapshot.ModeName}{(snapshot.IsReadOnly ? _statusMarkerReadOnly : string.Empty)}  {snapshot.StatusLine}";
        int statusEnd = FillText(grid[statusRow], statusText, _statusStyle);

        cursor = null;
        if (inCommand)
        {
            cursor = new CursorState(statusRow, Math.Min(statusEnd, columns - 1));
        }
        else if (snapshot.CursorRow >= 0 && snapshot.CursorRow < paneRows && snapshot.CursorColumn >= 0
            && snapshot.CursorColumn < columns)
        {
            bool insert = snapshot.ModeName == "INSERT";
            if (!insert || snapshot.CursorVisible)
            {
                cursor = new CursorState(snapshot.CursorRow, snapshot.CursorColumn);
            }
        }

        return grid;
    }

    private static void FillRow(GridCell[] cells, TerminalRow row)
    {
        int column = 0;
        int lead = -1;
        foreach (TerminalCellRun run in row.Runs)
        {
            foreach (Rune rune in run.Text.EnumerateRunes())
            {
                if (Rune.IsControl(rune))
                {
                    continue;
                }

                int width = CharWidth.Of(rune);
                if (width == 0)
                {
                    if (lead >= 0)
                    {
                        cells[lead] = cells[lead] with { Text = cells[lead].Text + rune.ToString() };
                    }

                    continue;
                }

                if (column + width > cells.Length)
                {
                    return;
                }

                cells[column] = new GridCell(rune.ToString(), run.Style);
                if (width == 2)
                {
                    cells[column + 1] = new GridCell(string.Empty, run.Style);
                }

                lead = column;
                column += width;
            }
        }
    }

    private static int FillText(GridCell[] cells, string text, CellStyle style)
    {
        int column = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            int width = Math.Max(1, CharWidth.Of(rune));
            if (column + width > cells.Length)
            {
                break;
            }

            cells[column] = new GridCell(rune.ToString(), style);
            if (width == 2)
            {
                cells[column + 1] = new GridCell(string.Empty, style);
            }

            column += width;
        }

        return column;
    }

    private static string Sgr(CellStyle style)
    {
        if (style == _plain)
        {
            return _reset;
        }

        var parts = new List<string> { "0" };
        CellAttributes attributes = style.Attributes;
        if (attributes.HasFlag(CellAttributes.Bold))
        {
            parts.Add("1");
        }

        if (attributes.HasFlag(CellAttributes.Dim))
        {
            parts.Add("2");
        }

        if (attributes.HasFlag(CellAttributes.Italic))
        {
            parts.Add("3");
        }

        if (attributes.HasFlag(CellAttributes.Underline))
        {
            parts.Add("4");
        }

        if (attributes.HasFlag(CellAttributes.Reverse))
        {
            parts.Add("7");
        }

        if (attributes.HasFlag(CellAttributes.Hidden))
        {
            parts.Add("8");
        }

        if (attributes.HasFlag(CellAttributes.Strikethrough))
        {
            parts.Add("9");
        }

        AppendColor(parts, style.Foreground, background: false);
        AppendColor(parts, style.Background, background: true);
        return "\u001b[" + string.Join(';', parts) + "m";
    }

    private static void AppendColor(List<string> parts, TermColor color, bool background)
    {
        switch (color.Kind)
        {
            case TermColorKind.Indexed when color.Index < 8:
                parts.Add(Invariant(background ? 40 + color.Index : 30 + color.Index));
                break;
            case TermColorKind.Indexed when color.Index < 16:
                parts.Add(Invariant(background ? 100 + color.Index - 8 : 90 + color.Index - 8));
                break;
            case TermColorKind.Indexed:
                parts.Add($"{(background ? 48 : 38)};5;{color.Index}");
                break;
            case TermColorKind.Rgb:
                parts.Add($"{(background ? 48 : 38)};2;{color.Red};{color.Green};{color.Blue}");
                break;
        }
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One printable grid cell. A wide character's second cell has empty text.</summary>
    private readonly record struct GridCell(string Text, CellStyle Style);

    /// <summary>Where the cursor goes, as a zero-based row and column.</summary>
    private readonly record struct CursorState(int Row, int Column);
}
