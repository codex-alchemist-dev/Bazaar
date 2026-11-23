using System.Globalization;
using System.Text;

namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>Mouse reporting modes requested by the program (DECSET 1000, 1002, 1006). Flags only; no mouse is sent.</summary>
[Flags]
public enum MouseModes : byte
{
    /// <summary>No mouse reporting.</summary>
    None = 0,

    /// <summary>Button press and release (1000).</summary>
    Press = 1 << 0,

    /// <summary>Button drag (1002).</summary>
    Drag = 1 << 1,

    /// <summary>SGR encoding (1006).</summary>
    Sgr = 1 << 2,
}

/// <summary>
/// The screen model and the VT command semantics. It holds a primary screen with scrollback and an alternate screen
/// without, the cursor, the scroll region, the SGR pen, the modes, the title and hyperlinks. It is an <see cref="IVtSink"/>,
/// so a <see cref="VtParser"/> drives it directly. Device replies go out through <see cref="Reply"/>.
/// </summary>
public sealed class ScreenBuffer : IVtSink
{
    /// <summary>Hard cap on scrollback lines.</summary>
    public const int MaxScrollbackLines = 200_000;

    /// <summary>Largest accepted column or row count. Larger values are clamped.</summary>
    public const int MaxDimension = 1000;

    /// <summary>Title length cap, in code points.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>Hyperlink spans kept at once. The oldest is dropped first.</summary>
    public const int MaxHyperlinks = 1000;

    /// <summary>Longest accepted hyperlink URI, in UTF-16 units.</summary>
    public const int MaxHyperlinkUriLength = 2048;

    /// <summary>Longest grapheme cluster kept in one cell, in UTF-16 units. Extra combining marks are dropped.</summary>
    public const int MaxClusterLength = 32;

    private const int _tabWidth = 8;

    private static readonly string[] _asciiText = CreateAsciiText();

    private readonly Grid _primary;
    private readonly Grid _alternate;
    private readonly List<LinkSpan> _links = [];
    private readonly int _scrollbackLimit;
    private int _cols;
    private int _rows;
    private Grid _grid;
    private CellStyle _pen = CellStyle.Plain;
    private string? _openUri;

    /// <summary>Creates a screen of <paramref name="cols"/> by <paramref name="rows"/> cells.</summary>
    /// <param name="cols">Columns, clamped to 1 through <see cref="MaxDimension"/>.</param>
    /// <param name="rows">Rows, clamped to 1 through <see cref="MaxDimension"/>.</param>
    /// <param name="scrollbackLimit">Scrollback lines kept on the primary screen, clamped to 0 through <see cref="MaxScrollbackLines"/>.</param>
    public ScreenBuffer(int cols, int rows, int scrollbackLimit = 10000)
    {
        _cols = ClampDimension(cols);
        _rows = ClampDimension(rows);
        _scrollbackLimit = Math.Clamp(scrollbackLimit, 0, MaxScrollbackLines);
        _primary = new Grid(_cols, _rows, _scrollbackLimit);
        _alternate = new Grid(_cols, _rows, 0);
        _grid = _primary;
    }

    /// <summary>Device replies (DA1, DSR) for the session's input path. Not rate-limited here; see <see cref="Emulator"/>.</summary>
    public event Action<ReadOnlyMemory<byte>>? Reply;

    /// <summary>Visible columns.</summary>
    public int Cols => _cols;

    /// <summary>Visible rows.</summary>
    public int Rows => _rows;

    /// <summary>Scrollback lines currently kept. Always 0 on the alternate screen.</summary>
    public int ScrollbackCount => _grid.Scrollback.Count;

    /// <summary>True while the alternate screen is active.</summary>
    public bool IsAlternateScreen => ReferenceEquals(_grid, _alternate);

    /// <summary>Cursor row within the visible area, 0-based.</summary>
    public int CursorRow => _grid.CursorRow;

    /// <summary>Cursor column, 0-based.</summary>
    public int CursorCol => _grid.CursorCol;

    /// <summary>Sanitised window title (OSC 0 and 2).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>DECCKM (DECSET 1): application cursor keys.</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>DECSET 2004: bracketed paste.</summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>DECTCEM (DECSET 25): cursor visible.</summary>
    public bool CursorVisible { get; private set; } = true;

    /// <summary>DECAWM (DECSET 7): autowrap.</summary>
    public bool AutoWrap { get; private set; } = true;

    /// <summary>DECOM (DECSET 6): origin mode. Cursor addressing is relative to the scroll region.</summary>
    public bool OriginMode { get; private set; }

    /// <summary>Mouse reporting modes requested by the program. Recorded as flags only.</summary>
    public MouseModes MouseMode { get; private set; }

    /// <summary>
    /// Hyperlink spans on the active screen (OSC 8). Stored only, never opened. The row is absolute, counting
    /// scrollback first, the same addressing as <see cref="GetCell"/>.
    /// </summary>
    public IReadOnlyList<(string Uri, int Row, int StartCol, int EndCol)> Hyperlinks
    {
        get
        {
            var rowOf = new Dictionary<Line, int>();
            int scrollback = _grid.Scrollback.Count;
            for (int i = 0; i < scrollback; i++)
            {
                rowOf[_grid.Scrollback[i]] = i;
            }

            for (int r = 0; r < _rows; r++)
            {
                rowOf[_grid.Lines[r]] = scrollback + r;
            }

            var result = new List<(string Uri, int Row, int StartCol, int EndCol)>();
            foreach (LinkSpan span in _links)
            {
                if (ReferenceEquals(span.Grid, _grid) && rowOf.TryGetValue(span.Line, out int row))
                {
                    result.Add((span.Uri, row, span.Start, span.End));
                }
            }

            return result;
        }
    }

    /// <summary>The cell at an absolute row (scrollback first) and column. Out of range returns a blank cell.</summary>
    public Cell GetCell(int absoluteRow, int col)
    {
        Line? line = LineAt(absoluteRow);
        if (line is null || (uint)col >= (uint)line.Cells.Length)
        {
            return Cell.Blank(CellStyle.Plain);
        }

        return line.Cells[col];
    }

    /// <summary>Text of one line with trailing blanks trimmed. Out of range returns an empty string.</summary>
    public string GetLineText(int absoluteRow)
    {
        Line? line = LineAt(absoluteRow);
        if (line is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(line.Cells.Length);
        foreach (Cell cell in line.Cells)
        {
            if (!cell.IsTrailingHalf)
            {
                builder.Append(cell.Text);
            }
        }

        return builder.ToString().TrimEnd(' ');
    }

    /// <summary>Changes the screen size. The primary screen reflows wrapped lines; the alternate screen keeps its content.</summary>
    public void Resize(int cols, int rows)
    {
        int newCols = ClampDimension(cols);
        int newRows = ClampDimension(rows);
        if (newCols == _cols && newRows == _rows)
        {
            return;
        }

        ReflowPrimary(newCols, newRows);
        ResizeGrid(_alternate, newCols, newRows);
        _cols = newCols;
        _rows = newRows;
        _links.Clear();
    }

    /// <inheritdoc />
    public void Print(Rune rune)
    {
        int value = rune.Value;
        if (value < 0x20 || (value >= 0x7F && value < 0xA0))
        {
            return;
        }

        int width = CharWidth.Of(rune);
        if (width == 0)
        {
            AppendCombining(rune);
            return;
        }

        if (_grid.WrapPending)
        {
            WrapLine();
        }

        if (width == 2 && _grid.CursorCol == _cols - 1)
        {
            if (!AutoWrap)
            {
                return;
            }

            WrapLine();
        }

        Line line = _grid.Lines[_grid.CursorRow];
        Cell[] cells = line.Cells;
        int col = _grid.CursorCol;
        if (cells[col].IsTrailingHalf && col > 0)
        {
            cells[col - 1] = Cell.Blank(cells[col - 1].Style);
        }

        if (cells[col].Width == 2 && col + 1 < cells.Length)
        {
            cells[col + 1] = Cell.Blank(cells[col + 1].Style);
        }

        string text = value < _asciiText.Length ? _asciiText[value] : rune.ToString();
        cells[col] = new Cell(text, (byte)width, _pen);
        if (width == 2)
        {
            cells[col + 1] = new Cell(string.Empty, 0, _pen);
        }

        if (_openUri is not null)
        {
            RecordLink(line, col, width, _openUri);
        }

        int next = col + width;
        if (next >= _cols)
        {
            _grid.CursorCol = _cols - 1;
            _grid.WrapPending = AutoWrap;
        }
        else
        {
            _grid.CursorCol = next;
        }
    }

    /// <inheritdoc />
    public void Execute(byte control)
    {
        switch (control)
        {
            case 0x08:
                _grid.CursorCol = Math.Max(0, _grid.CursorCol - 1);
                _grid.WrapPending = false;
                break;
            case 0x09:
                _grid.CursorCol = Math.Min(_cols - 1, ((_grid.CursorCol / _tabWidth) + 1) * _tabWidth);
                _grid.WrapPending = false;
                break;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                LineFeed();
                break;
            case 0x0D:
                _grid.CursorCol = 0;
                _grid.WrapPending = false;
                break;
            default:
                break;
        }
    }

    /// <inheritdoc />
    public void EscDispatch(byte intermediate, byte final)
    {
        if (intermediate != 0)
        {
            return;
        }

        switch ((char)final)
        {
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                _grid.CursorCol = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
        }
    }

    /// <inheritdoc />
    public void CsiDispatch(CsiSequence sequence)
    {
        if (sequence.Intermediate != 0)
        {
            return;
        }

        switch ((char)sequence.Final)
        {
            case 'A':
                CursorUp(sequence.Get(0, 1));
                break;
            case 'B':
                CursorDown(sequence.Get(0, 1));
                break;
            case 'C':
                CursorForward(sequence.Get(0, 1));
                break;
            case 'D':
                CursorBack(sequence.Get(0, 1));
                break;
            case 'E':
                CursorDown(sequence.Get(0, 1));
                _grid.CursorCol = 0;
                break;
            case 'F':
                CursorUp(sequence.Get(0, 1));
                _grid.CursorCol = 0;
                break;
            case 'G':
                MoveColumn(sequence.Get(0, 1) - 1);
                break;
            case 'H':
            case 'f':
                SetCursorPosition(sequence.Get(0, 1) - 1, sequence.Get(1, 1) - 1);
                break;
            case 'd':
                SetCursorPosition(sequence.Get(0, 1) - 1, _grid.CursorCol);
                break;
            case 'J':
                EraseDisplay(sequence.Get(0, 0));
                break;
            case 'K':
                EraseLine(sequence.Get(0, 0));
                break;
            case 'X':
                EraseCells(_grid.CursorRow, _grid.CursorCol, _grid.CursorCol + sequence.Get(0, 1));
                break;
            case '@':
                InsertChars(sequence.Get(0, 1));
                break;
            case 'P':
                DeleteChars(sequence.Get(0, 1));
                break;
            case 'L':
                InsertLines(sequence.Get(0, 1));
                break;
            case 'M':
                DeleteLines(sequence.Get(0, 1));
                break;
            case 'S':
                if (sequence.Prefix == 0)
                {
                    ScrollUpRegion(sequence.Get(0, 1));
                }

                break;
            case 'T':
                if (sequence.Prefix == 0 && sequence.Count <= 1)
                {
                    ShiftDown(_grid, _grid.Top, _grid.Bottom, Math.Min(sequence.Get(0, 1), _grid.Bottom - _grid.Top + 1));
                }

                break;
            case 'r':
                if (sequence.Prefix == 0)
                {
                    SetScrollRegion(sequence);
                }

                break;
            case 's':
                if (sequence.Prefix == 0 && sequence.Raw(0) == 0)
                {
                    SaveCursor();
                }

                break;
            case 'u':
                if (sequence.Prefix == 0 && sequence.Raw(0) == 0)
                {
                    RestoreCursor();
                }

                break;
            case 'm':
                if (sequence.Prefix == 0)
                {
                    Sgr(sequence);
                }

                break;
            case 'h':
            case 'l':
                if (sequence.Prefix == (byte)'?')
                {
                    for (int i = 0; i < sequence.Count; i++)
                    {
                        SetPrivateMode(sequence.Raw(i), sequence.Final == (byte)'h');
                    }
                }

                break;
            case 'n':
                if (sequence.Prefix == 0)
                {
                    DeviceStatusReport(sequence.Raw(0));
                }

                break;
            case 'c':
                if (sequence.Prefix == 0 && sequence.Raw(0) == 0)
                {
                    SendReply("\u001b[?62;22c");
                }

                break;
            default:
                break;
        }
    }

    /// <inheritdoc />
    public void OscDispatch(ReadOnlySpan<byte> payload)
    {
        int separator = payload.IndexOf((byte)';');
        ReadOnlySpan<byte> number = separator < 0 ? payload : payload[..separator];
        ReadOnlySpan<byte> rest = separator < 0 ? ReadOnlySpan<byte>.Empty : payload[(separator + 1)..];
        if (number.SequenceEqual("0"u8) || number.SequenceEqual("2"u8))
        {
            SetTitle(rest);
        }
        else if (number.SequenceEqual("8"u8))
        {
            SetHyperlink(rest);
        }

        // OSC 52 (clipboard), icon name and every other OSC are refused.
    }

    private static string[] CreateAsciiText()
    {
        var text = new string[128];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = ((char)i).ToString();
        }

        return text;
    }

    private static int ClampDimension(int value) => Math.Clamp(value, 1, MaxDimension);

    private static byte ToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static bool IsPlainBlank(Cell cell) => cell.Width == 1 && cell.Text == " " && cell.Style == CellStyle.Plain;

    private CellStyle EraseStyle => new(TermColor.Default, _pen.Background, CellAttributes.None);

    private Line? LineAt(int absoluteRow)
    {
        if (absoluteRow < 0)
        {
            return null;
        }

        int scrollback = _grid.Scrollback.Count;
        if (absoluteRow < scrollback)
        {
            return _grid.Scrollback[absoluteRow];
        }

        int row = absoluteRow - scrollback;
        return row < _rows ? _grid.Lines[row] : null;
    }

    private void WrapLine()
    {
        _grid.Lines[_grid.CursorRow].Wrapped = true;
        _grid.CursorCol = 0;
        LineFeed();
    }

    private void AppendCombining(Rune rune)
    {
        Line line = _grid.Lines[_grid.CursorRow];
        int index = _grid.WrapPending ? _grid.CursorCol : _grid.CursorCol - 1;
        if (index >= 0 && line.Cells[index].IsTrailingHalf)
        {
            index--;
        }

        if (index < 0)
        {
            return;
        }

        Cell cell = line.Cells[index];
        if (cell.Width == 0 || cell.Text.Length + rune.Utf16SequenceLength > MaxClusterLength)
        {
            return;
        }

        line.Cells[index] = new Cell(cell.Text + rune.ToString(), cell.Width, cell.Style);
    }

    private void RecordLink(Line line, int col, int width, string uri)
    {
        int end = col + width - 1;
        if (_links.Count > 0)
        {
            LinkSpan last = _links[^1];
            if (ReferenceEquals(last.Grid, _grid) && ReferenceEquals(last.Line, line) && last.End == col - 1 && last.Uri == uri)
            {
                last.End = end;
                return;
            }
        }

        _links.Add(new LinkSpan(_grid, line, uri, col, end));
        if (_links.Count > MaxHyperlinks)
        {
            _links.RemoveAt(0);
        }
    }

    private void SetTitle(ReadOnlySpan<byte> payload)
    {
        string text = Encoding.UTF8.GetString(payload);
        if (text == "?")
        {
            return;
        }

        var builder = new StringBuilder();
        int runes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (runes >= MaxTitleLength)
            {
                break;
            }

            builder.Append(rune.ToString().AsSpan());
            runes++;
        }

        Title = builder.ToString();
    }

    private void SetHyperlink(ReadOnlySpan<byte> payload)
    {
        int separator = payload.IndexOf((byte)';');
        if (separator < 0)
        {
            return;
        }

        ReadOnlySpan<byte> uriBytes = payload[(separator + 1)..];
        if (uriBytes.IsEmpty)
        {
            _openUri = null;
            return;
        }

        var builder = new StringBuilder();
        foreach (Rune rune in Encoding.UTF8.GetString(uriBytes).EnumerateRunes())
        {
            if (!Rune.IsControl(rune))
            {
                builder.Append(rune.ToString().AsSpan());
            }
        }

        _openUri = builder.Length == 0 || builder.Length > MaxHyperlinkUriLength ? null : builder.ToString();
    }

    private void LineFeed()
    {
        _grid.WrapPending = false;
        if (_grid.CursorRow == _grid.Bottom)
        {
            ScrollUpRegion(1);
        }
        else if (_grid.CursorRow < _rows - 1)
        {
            _grid.CursorRow++;
        }
    }

    private void ReverseIndex()
    {
        _grid.WrapPending = false;
        if (_grid.CursorRow == _grid.Top)
        {
            ShiftDown(_grid, _grid.Top, _grid.Bottom, 1);
        }
        else if (_grid.CursorRow > 0)
        {
            _grid.CursorRow--;
        }
    }

    private void SetCursorPosition(int row, int col)
    {
        _grid.WrapPending = false;
        if (OriginMode)
        {
            _grid.CursorRow = Math.Clamp(row + _grid.Top, _grid.Top, _grid.Bottom);
        }
        else
        {
            _grid.CursorRow = Math.Clamp(row, 0, _rows - 1);
        }

        _grid.CursorCol = Math.Clamp(col, 0, _cols - 1);
    }

    private void MoveColumn(int col)
    {
        _grid.WrapPending = false;
        _grid.CursorCol = Math.Clamp(col, 0, _cols - 1);
    }

    private void CursorUp(int count)
    {
        _grid.WrapPending = false;
        int limit = _grid.CursorRow >= _grid.Top && _grid.CursorRow <= _grid.Bottom ? _grid.Top : 0;
        _grid.CursorRow = Math.Max(_grid.CursorRow - count, limit);
    }

    private void CursorDown(int count)
    {
        _grid.WrapPending = false;
        int limit = _grid.CursorRow >= _grid.Top && _grid.CursorRow <= _grid.Bottom ? _grid.Bottom : _rows - 1;
        _grid.CursorRow = Math.Min(_grid.CursorRow + count, limit);
    }

    private void CursorForward(int count)
    {
        _grid.WrapPending = false;
        _grid.CursorCol = Math.Min(_grid.CursorCol + count, _cols - 1);
    }

    private void CursorBack(int count)
    {
        _grid.WrapPending = false;
        _grid.CursorCol = Math.Max(_grid.CursorCol - count, 0);
    }

    private void SetScrollRegion(CsiSequence sequence)
    {
        int top = sequence.Get(0, 1) - 1;
        int bottom = Math.Min(sequence.Get(1, _rows) - 1, _rows - 1);
        if (top < 0 || top >= bottom)
        {
            return;
        }

        _grid.Top = top;
        _grid.Bottom = bottom;
        SetCursorPosition(0, 0);
    }

    private void DeviceStatusReport(int code)
    {
        switch (code)
        {
            case 5:
                SendReply("\u001b[0n");
                break;
            case 6:
                int row = _grid.CursorRow + 1 - (OriginMode ? _grid.Top : 0);
                SendReply($"\u001b[{row.ToString(CultureInfo.InvariantCulture)};{(_grid.CursorCol + 1).ToString(CultureInfo.InvariantCulture)}R");
                break;
        }
    }

    private void SendReply(string text) => Reply?.Invoke(Encoding.ASCII.GetBytes(text));

    private void EraseDisplay(int mode)
    {
        _grid.WrapPending = false;
        switch (mode)
        {
            case 0:
                EraseCells(_grid.CursorRow, _grid.CursorCol, _cols);
                for (int r = _grid.CursorRow + 1; r < _rows; r++)
                {
                    EraseCells(r, 0, _cols);
                }

                break;
            case 1:
                for (int r = 0; r < _grid.CursorRow; r++)
                {
                    EraseCells(r, 0, _cols);
                }

                EraseCells(_grid.CursorRow, 0, _grid.CursorCol + 1);
                break;
            case 2:
                for (int r = 0; r < _rows; r++)
                {
                    EraseCells(r, 0, _cols);
                }

                break;
        }
    }

    private void EraseLine(int mode)
    {
        _grid.WrapPending = false;
        switch (mode)
        {
            case 0:
                EraseCells(_grid.CursorRow, _grid.CursorCol, _cols);
                break;
            case 1:
                EraseCells(_grid.CursorRow, 0, _grid.CursorCol + 1);
                break;
            case 2:
                EraseCells(_grid.CursorRow, 0, _cols);
                break;
        }
    }

    private void EraseCells(int row, int from, int to)
    {
        Line line = _grid.Lines[row];
        CellStyle style = EraseStyle;
        int end = Math.Min(to, _cols);
        for (int c = Math.Max(0, from); c < end; c++)
        {
            line.Cells[c] = Cell.Blank(style);
        }

        Repair(line.Cells);
    }

    private void InsertChars(int count)
    {
        _grid.WrapPending = false;
        Cell[] cells = _grid.Lines[_grid.CursorRow].Cells;
        int col = _grid.CursorCol;
        int shift = Math.Min(count, _cols - col);
        for (int c = _cols - 1; c >= col + shift; c--)
        {
            cells[c] = cells[c - shift];
        }

        for (int c = col; c < col + shift; c++)
        {
            cells[c] = Cell.Blank(EraseStyle);
        }

        Repair(cells);
    }

    private void DeleteChars(int count)
    {
        _grid.WrapPending = false;
        Cell[] cells = _grid.Lines[_grid.CursorRow].Cells;
        int col = _grid.CursorCol;
        int shift = Math.Min(count, _cols - col);
        for (int c = col; c < _cols - shift; c++)
        {
            cells[c] = cells[c + shift];
        }

        for (int c = _cols - shift; c < _cols; c++)
        {
            cells[c] = Cell.Blank(EraseStyle);
        }

        Repair(cells);
    }

    private void InsertLines(int count)
    {
        if (_grid.CursorRow < _grid.Top || _grid.CursorRow > _grid.Bottom)
        {
            return;
        }

        _grid.WrapPending = false;
        _grid.CursorCol = 0;
        ShiftDown(_grid, _grid.CursorRow, _grid.Bottom, Math.Min(count, _grid.Bottom - _grid.CursorRow + 1));
    }

    private void DeleteLines(int count)
    {
        if (_grid.CursorRow < _grid.Top || _grid.CursorRow > _grid.Bottom)
        {
            return;
        }

        _grid.WrapPending = false;
        _grid.CursorCol = 0;
        ShiftUp(_grid, _grid.CursorRow, _grid.Bottom, Math.Min(count, _grid.Bottom - _grid.CursorRow + 1));
    }

    private void ScrollUpRegion(int count)
    {
        Grid grid = _grid;
        count = Math.Min(count, grid.Bottom - grid.Top + 1);
        if (grid.Scrollback.Capacity > 0 && grid.Top == 0 && grid.Bottom == _rows - 1)
        {
            for (int r = 0; r < count; r++)
            {
                grid.Scrollback.Add(grid.Lines[r]);
            }
        }

        ShiftUp(grid, grid.Top, grid.Bottom, count);
    }

    private void ShiftUp(Grid grid, int top, int bottom, int count)
    {
        for (int r = top; r + count <= bottom; r++)
        {
            grid.Lines[r] = grid.Lines[r + count];
        }

        for (int r = Math.Max(top, bottom - count + 1); r <= bottom; r++)
        {
            grid.Lines[r] = new Line(_cols, EraseStyle);
        }
    }

    private void ShiftDown(Grid grid, int top, int bottom, int count)
    {
        for (int r = bottom; r - count >= top; r--)
        {
            grid.Lines[r] = grid.Lines[r - count];
        }

        for (int r = top; r < Math.Min(top + count, bottom + 1); r++)
        {
            grid.Lines[r] = new Line(_cols, EraseStyle);
        }
    }

    private void SetPrivateMode(int mode, bool enable)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = enable;
                break;
            case 6:
                OriginMode = enable;
                SetCursorPosition(0, 0);
                break;
            case 7:
                AutoWrap = enable;
                if (!enable)
                {
                    _grid.WrapPending = false;
                }

                break;
            case 25:
                CursorVisible = enable;
                break;
            case 47:
            case 1047:
            case 1049:
                if (enable)
                {
                    EnterAlternate(mode);
                }
                else
                {
                    LeaveAlternate(mode);
                }

                break;
            case 1000:
                MouseMode = enable ? MouseMode | MouseModes.Press : MouseMode & ~MouseModes.Press;
                break;
            case 1002:
                MouseMode = enable ? MouseMode | MouseModes.Drag : MouseMode & ~MouseModes.Drag;
                break;
            case 1006:
                MouseMode = enable ? MouseMode | MouseModes.Sgr : MouseMode & ~MouseModes.Sgr;
                break;
            case 2004:
                BracketedPaste = enable;
                break;
        }
    }

    private void EnterAlternate(int mode)
    {
        if (IsAlternateScreen)
        {
            return;
        }

        if (mode == 1049)
        {
            SaveCursor();
        }

        _grid = _alternate;
        _alternate.Top = 0;
        _alternate.Bottom = _rows - 1;
        _alternate.WrapPending = false;
        if (mode != 47)
        {
            for (int r = 0; r < _rows; r++)
            {
                _alternate.Lines[r] = new Line(_cols, CellStyle.Plain);
            }

            _alternate.CursorRow = 0;
            _alternate.CursorCol = 0;
        }
    }

    private void LeaveAlternate(int mode)
    {
        if (!IsAlternateScreen)
        {
            return;
        }

        _grid = _primary;
        if (mode == 1049)
        {
            RestoreCursor();
        }
    }

    private void SaveCursor()
    {
        _grid.Saved = new SavedCursor(_grid.CursorRow, _grid.CursorCol, _pen, _grid.WrapPending, OriginMode);
    }

    private void RestoreCursor()
    {
        Grid grid = _grid;
        if (grid.Saved is not { } saved)
        {
            grid.CursorRow = 0;
            grid.CursorCol = 0;
            grid.WrapPending = false;
            return;
        }

        grid.CursorRow = Math.Clamp(saved.Row, 0, _rows - 1);
        grid.CursorCol = Math.Clamp(saved.Col, 0, _cols - 1);
        grid.WrapPending = saved.WrapPending;
        _pen = saved.Pen;
        OriginMode = saved.Origin;
    }

    private void Sgr(CsiSequence sequence)
    {
        if (sequence.Count == 0)
        {
            _pen = CellStyle.Plain;
            return;
        }

        TermColor foreground = _pen.Foreground;
        TermColor background = _pen.Background;
        CellAttributes attributes = _pen.Attributes;
        for (int i = 0; i < sequence.Count; i++)
        {
            int value = sequence.Raw(i);
            switch (value)
            {
                case 0:
                    foreground = TermColor.Default;
                    background = TermColor.Default;
                    attributes = CellAttributes.None;
                    break;
                case 1:
                    attributes |= CellAttributes.Bold;
                    break;
                case 2:
                    attributes |= CellAttributes.Dim;
                    break;
                case 3:
                    attributes |= CellAttributes.Italic;
                    break;
                case 4:
                    attributes |= CellAttributes.Underline;
                    break;
                case 7:
                    attributes |= CellAttributes.Reverse;
                    break;
                case 8:
                    attributes |= CellAttributes.Hidden;
                    break;
                case 9:
                    attributes |= CellAttributes.Strikethrough;
                    break;
                case 22:
                    attributes &= ~(CellAttributes.Bold | CellAttributes.Dim);
                    break;
                case 23:
                    attributes &= ~CellAttributes.Italic;
                    break;
                case 24:
                    attributes &= ~CellAttributes.Underline;
                    break;
                case 27:
                    attributes &= ~CellAttributes.Reverse;
                    break;
                case 28:
                    attributes &= ~CellAttributes.Hidden;
                    break;
                case 29:
                    attributes &= ~CellAttributes.Strikethrough;
                    break;
                case 38:
                    i = ExtendedColor(sequence, i, ref foreground);
                    break;
                case 48:
                    i = ExtendedColor(sequence, i, ref background);
                    break;
                case 39:
                    foreground = TermColor.Default;
                    break;
                case 49:
                    background = TermColor.Default;
                    break;
                default:
                    if (value is >= 30 and <= 37)
                    {
                        foreground = TermColor.FromIndex((byte)(value - 30));
                    }
                    else if (value is >= 40 and <= 47)
                    {
                        background = TermColor.FromIndex((byte)(value - 40));
                    }
                    else if (value is >= 90 and <= 97)
                    {
                        foreground = TermColor.FromIndex((byte)(value - 82));
                    }
                    else if (value is >= 100 and <= 107)
                    {
                        background = TermColor.FromIndex((byte)(value - 92));
                    }

                    break;
            }
        }

        _pen = new CellStyle(foreground, background, attributes);
    }

    private static int ExtendedColor(CsiSequence sequence, int index, ref TermColor target)
    {
        int mode = sequence.Raw(index + 1);
        if (index + 1 < sequence.Count && mode == 5 && index + 2 < sequence.Count)
        {
            target = TermColor.FromIndex(ToByte(sequence.Raw(index + 2)));
            return index + 2;
        }

        if (index + 1 < sequence.Count && mode == 2 && index + 4 < sequence.Count)
        {
            target = TermColor.FromRgb(ToByte(sequence.Raw(index + 2)), ToByte(sequence.Raw(index + 3)), ToByte(sequence.Raw(index + 4)));
            return index + 4;
        }

        return index;
    }

    private void ReflowPrimary(int newCols, int newRows)
    {
        Grid grid = _primary;
        int scrollback = grid.Scrollback.Count;
        var all = new List<Line>(scrollback + grid.Lines.Length);
        for (int i = 0; i < scrollback; i++)
        {
            all.Add(grid.Scrollback[i]);
        }

        all.AddRange(grid.Lines);
        int cursorIndex = scrollback + grid.CursorRow;
        int cursorCol = grid.CursorCol;
        var output = new List<Line>();
        int cursorOutRow = -1;
        int cursorOutCol = 0;

        int start = 0;
        while (start < all.Count)
        {
            int end = start;
            while (end < all.Count - 1 && all[end].Wrapped)
            {
                end++;
            }

            bool holdsCursor = cursorIndex >= start && cursorIndex <= end;
            var glyphs = new List<Cell>();
            int cursorGlyph = -1;
            for (int li = start; li <= end; li++)
            {
                Cell[] cells = all[li].Cells;
                for (int c = 0; c < cells.Length; c++)
                {
                    Cell cell = cells[c];
                    bool isCursor = holdsCursor && li == cursorIndex && c == cursorCol;
                    if (cell.IsTrailingHalf)
                    {
                        if (isCursor)
                        {
                            cursorGlyph = glyphs.Count - 1;
                        }

                        continue;
                    }

                    if (isCursor)
                    {
                        cursorGlyph = glyphs.Count;
                    }

                    glyphs.Add(cell);
                }
            }

            int keep = holdsCursor && cursorGlyph >= 0 ? cursorGlyph + 1 : 0;
            while (glyphs.Count > keep && IsPlainBlank(glyphs[^1]))
            {
                glyphs.RemoveAt(glyphs.Count - 1);
            }

            var current = new Line(newCols, CellStyle.Plain);
            int col = 0;
            for (int g = 0; g < glyphs.Count; g++)
            {
                Cell glyph = glyphs[g];
                int width = glyph.Width;
                if (width > newCols)
                {
                    glyph = new Cell(" ", 1, glyph.Style);
                    width = 1;
                }

                if (col + width > newCols)
                {
                    current.Wrapped = true;
                    output.Add(current);
                    current = new Line(newCols, CellStyle.Plain);
                    col = 0;
                }

                if (holdsCursor && g == cursorGlyph)
                {
                    cursorOutRow = output.Count;
                    cursorOutCol = col;
                }

                current.Cells[col] = glyph;
                if (width == 2)
                {
                    current.Cells[col + 1] = new Cell(string.Empty, 0, glyph.Style);
                }

                col += width;
            }

            if (holdsCursor && cursorGlyph >= glyphs.Count)
            {
                cursorOutRow = output.Count;
                cursorOutCol = Math.Min(col, newCols - 1);
            }

            output.Add(current);
            start = end + 1;
        }

        if (cursorOutRow < 0)
        {
            cursorOutRow = Math.Max(0, output.Count - 1);
            cursorOutCol = 0;
        }

        // Blank rows below the cursor are not content: drop them so reflow does not push the cursor's text into scrollback.
        while (output.Count - 1 > cursorOutRow && output[^1].Cells.All(IsPlainBlank))
        {
            output.RemoveAt(output.Count - 1);
        }

        while (output.Count < newRows)
        {
            output.Add(new Line(newCols, CellStyle.Plain));
        }

        int visibleStart = output.Count - newRows;
        var ring = new LineRing(_scrollbackLimit);
        for (int i = 0; i < visibleStart; i++)
        {
            ring.Add(output[i]);
        }

        grid.Scrollback = ring;
        grid.Lines = output.GetRange(visibleStart, newRows).ToArray();
        grid.CursorRow = Math.Clamp(cursorOutRow - visibleStart, 0, newRows - 1);
        grid.CursorCol = Math.Clamp(cursorOutCol, 0, newCols - 1);
        grid.WrapPending = false;
        grid.Top = 0;
        grid.Bottom = newRows - 1;
    }

    private static void ResizeGrid(Grid grid, int newCols, int newRows)
    {
        var lines = new Line[newRows];
        for (int r = 0; r < newRows; r++)
        {
            var line = new Line(newCols, CellStyle.Plain);
            if (r < grid.Lines.Length)
            {
                Cell[] source = grid.Lines[r].Cells;
                Array.Copy(source, line.Cells, Math.Min(source.Length, newCols));
                Repair(line.Cells);
            }

            lines[r] = line;
        }

        grid.Lines = lines;
        grid.Top = 0;
        grid.Bottom = newRows - 1;
        grid.WrapPending = false;
        grid.CursorRow = Math.Clamp(grid.CursorRow, 0, newRows - 1);
        grid.CursorCol = Math.Clamp(grid.CursorCol, 0, newCols - 1);
    }

    /// <summary>Clears wide-character halves that lost their partner, so no cell is orphaned.</summary>
    private static void Repair(Cell[] cells)
    {
        for (int c = 0; c < cells.Length; c++)
        {
            Cell cell = cells[c];
            if (cell.Width == 2 && (c + 1 >= cells.Length || !cells[c + 1].IsTrailingHalf))
            {
                cells[c] = Cell.Blank(cell.Style);
            }
            else if (cell.IsTrailingHalf && (c == 0 || cells[c - 1].Width != 2))
            {
                cells[c] = Cell.Blank(cell.Style);
            }
        }
    }

    private sealed class Line(int cols, CellStyle style)
    {
        public Cell[] Cells { get; } = FillBlank(cols, style);

        public bool Wrapped { get; set; }

        private static Cell[] FillBlank(int cols, CellStyle style)
        {
            var cells = new Cell[cols];
            Array.Fill(cells, Cell.Blank(style));
            return cells;
        }
    }

    /// <summary>Fixed-capacity ring of lines. Holds the oldest lines out once full. Capacity 0 keeps nothing.</summary>
    private sealed class LineRing(int capacity)
    {
        private readonly Line[] _items = new Line[capacity];
        private int _head;

        public int Capacity => _items.Length;

        public int Count { get; private set; }

        public Line this[int index] => _items[(_head + index) % _items.Length];

        public void Add(Line line)
        {
            if (_items.Length == 0)
            {
                return;
            }

            if (Count < _items.Length)
            {
                _items[(_head + Count) % _items.Length] = line;
                Count++;
            }
            else
            {
                _items[_head] = line;
                _head = (_head + 1) % _items.Length;
            }
        }
    }

    private sealed class Grid(int cols, int rows, int scrollbackLimit)
    {
        public Line[] Lines { get; set; } = CreateLines(cols, rows);

        public LineRing Scrollback { get; set; } = new(scrollbackLimit);

        public int CursorRow { get; set; }

        public int CursorCol { get; set; }

        public bool WrapPending { get; set; }

        public int Top { get; set; }

        public int Bottom { get; set; } = rows - 1;

        public SavedCursor? Saved { get; set; }

        private static Line[] CreateLines(int cols, int rows)
        {
            var lines = new Line[rows];
            for (int r = 0; r < rows; r++)
            {
                lines[r] = new Line(cols, CellStyle.Plain);
            }

            return lines;
        }
    }

    private readonly record struct SavedCursor(int Row, int Col, CellStyle Pen, bool WrapPending, bool Origin);

    private sealed class LinkSpan(Grid grid, Line line, string uri, int start, int end)
    {
        public Grid Grid { get; } = grid;

        public Line Line { get; } = line;

        public string Uri { get; } = uri;

        public int Start { get; } = start;

        public int End { get; set; } = end;
    }
}
