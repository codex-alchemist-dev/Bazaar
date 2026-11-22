using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Bazaar.Client.Calcinato.Emulation;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calcinato.Session;

namespace Bazaar.Client.Calcinato.Avalonia;

/// <summary>
/// Draws a <see cref="TerminalPaneViewModel"/> as a monospace cell grid (doc 55, section 5.4) and sends the person's keys,
/// paste and resizes to the <see cref="TerminalSessionManager"/> the pane follows.
/// </summary>
/// <remarks>
/// The bottom row is the status line. Cell size is measured from the theme font (<c>Terminal.FontFamily</c>,
/// <c>Terminal.FontSize</c>). The terminal grid is the bounds divided by the cell size, less the status row, and a size
/// change resizes the active session after <see cref="ResizeDebounce"/>. Keys are applied in the order they arrived.
/// Output and accessibility updates arrive on other threads and are marshalled to the UI thread.
/// </remarks>
public sealed class TerminalControl : Control
{
    private const string _fontFamilyToken = "Terminal.FontFamily";
    private const string _fontSizeToken = "Terminal.FontSize";
    private const int _defaultCols = 80;
    private const int _defaultRows = 24;
    private const string _fallbackFontFamily = "Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, monospace";
    private const double _fallbackFontSize = 13;

    private readonly TerminalPaneViewModel _pane;
    private readonly object _inputGate = new();
    private Task _inputTail = Task.CompletedTask;
    private DispatcherTimer? _resizeTimer;
    private int _pendingCols;
    private int _pendingRows;
    private int _appliedCols;
    private int _appliedRows;
    private TerminalSession? _appliedSession;
    private bool _subscribed;
    private Size _arrangedSize;

    /// <summary>Creates the control for a terminal pane.</summary>
    /// <param name="pane">The pane to draw. Its manager receives keys, paste and resizes.</param>
    public TerminalControl(TerminalPaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        _pane = pane;
        Focusable = true;
        AutomationProperties.SetName(this, "Terminal");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    /// <summary>
    /// How long a size change waits before the session is resized. Zero resizes at once. The default keeps a window drag
    /// from sending a resize for every pixel.
    /// </summary>
    public TimeSpan ResizeDebounce { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The last error from sending input or resizing, or null. The session's status line shows terminal output errors.</summary>
    public string? LastError { get; private set; }

    /// <summary>The pane this control draws.</summary>
    public TerminalPaneViewModel Pane => _pane;

    /// <summary>
    /// Completes when every key, paste and resize queued so far has been applied to the session. Heads do not need it;
    /// tests use it to wait for input.
    /// </summary>
    public Task InputApplied
    {
        get
        {
            lock (_inputGate)
            {
                return _inputTail;
            }
        }
    }

    /// <summary>The cell grid the bounds give: columns and rows of text, not counting the status row.</summary>
    /// <param name="bounds">Size of the control.</param>
    /// <param name="metrics">Cell size from the theme font.</param>
    public static (int Cols, int Rows) GridFor(Size bounds, CellSize metrics)
    {
        int cols = Math.Max(1, (int)Math.Floor(bounds.Width / metrics.Width));
        int rows = Math.Max(1, (int)Math.Floor(bounds.Height / metrics.Height) - 1);
        return (cols, rows);
    }

    /// <summary>Measures the cell size from the theme font. Public so a head can size its layout the same way.</summary>
    public static CellSize MeasureCell() => MeasureCell(ThemeFont());

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_subscribed)
        {
            _pane.PropertyChanged += OnPanePropertyChanged;
            _subscribed = true;
        }

        RequestResize();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed)
        {
            _pane.PropertyChanged -= OnPanePropertyChanged;
            _subscribed = false;
        }

        _resizeTimer?.Stop();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        CellSize cell = MeasureCell();
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : _defaultCols * cell.Width;
        double height = double.IsFinite(availableSize.Height) ? availableSize.Height : (_defaultRows + 1) * cell.Height;
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Bounds still hold the previous size during arrange, so the new size is passed on from here.
        Size size = base.ArrangeOverride(finalSize);
        _arrangedSize = size;
        RequestResize();
        return size;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync();
            return;
        }

        if (TerminalKeyMapper.TryMapKey(e.Key, e.KeyModifiers, out TerminalKey key))
        {
            e.Handled = true;
            Send(key);
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (TerminalKeyMapper.FromText(e.Text, out TerminalKey key))
        {
            e.Handled = true;
            Send(key);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    /// <summary>
    /// Pastes text into the active session. Bracketed paste is used when the program has turned it on. Paste is sent in
    /// Insert mode only, and a read-only viewer is refused by the session.
    /// </summary>
    /// <param name="text">The text to paste.</param>
    public void Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        TerminalSession? session = _pane.Manager?.Active;
        if (session is null || text.Length == 0 || session.Engine.Mode != ModalMode.Insert)
        {
            return;
        }

        Enqueue(() => PasteAsync(session, text));
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);

        TerminalPalette palette = TerminalPalette.FromTheme();
        CellSize cell = MeasureCell();
        TerminalPaneSnapshot snap = _pane.Snapshot;
        (int cols, int rows) = GridFor(Bounds.Size, cell);
        int drawRows = Math.Min(rows, snap.Rows.Count);

        context.DrawRectangle(Brush(palette.Background), null, new Rect(Bounds.Size));

        for (int r = 0; r < drawRows; r++)
        {
            DrawRow(context, cell, palette, snap, r, cols);
        }

        DrawCursor(context, cell, palette, snap, drawRows, cols);
        DrawStatusRow(context, cell, palette, snap, rows, cols);
    }

    /// <summary>The colour of one cell after reverse video, selection and hidden text are applied.</summary>
    /// <param name="style">The cell's pen state.</param>
    /// <param name="palette">The colours to resolve against.</param>
    /// <param name="selected">True when the cell is inside the Visual selection.</param>
    public static (Color Foreground, Color Background) ResolveCellColors(CellStyle style, TerminalPalette palette, bool selected)
    {
        ArgumentNullException.ThrowIfNull(palette);

        Color fg = palette.Resolve(style.Foreground, foreground: true);
        Color bg = palette.Resolve(style.Background, foreground: false);
        if (style.Attributes.HasFlag(CellAttributes.Reverse))
        {
            (fg, bg) = (bg, fg);
        }

        if (selected)
        {
            bg = palette.Selection;
        }

        if (style.Attributes.HasFlag(CellAttributes.Dim))
        {
            fg = Color.FromArgb(128, fg.R, fg.G, fg.B);
        }

        if (style.Attributes.HasFlag(CellAttributes.Hidden))
        {
            fg = bg;
        }

        return (fg, bg);
    }

    /// <summary>Splits a row into the cells it occupies. A wide character takes two columns; its trailing half is not listed.</summary>
    /// <param name="row">The row to split.</param>
    public static IEnumerable<TerminalCluster> Clusters(TerminalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        int column = 0;
        foreach (TerminalCellRun run in row.Runs)
        {
            var elements = StringInfo.GetTextElementEnumerator(run.Text);
            while (elements.MoveNext())
            {
                string text = (string)elements.Current;
                int width = Math.Max(1, CharWidth.Of(Rune.GetRuneAt(text, 0)));
                yield return new TerminalCluster(column, width, text, run.Style);
                column += width;
            }
        }
    }

    // Pane changes arrive on the thread that made them. The UI thread redraws and updates the automation peer.
    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        Dispatcher.UIThread.Post(() => OnPaneChanged(name));
    }

    private void OnPaneChanged(string? propertyName)
    {
        if (VisualRoot is null)
        {
            return;
        }

        if (propertyName == nameof(TerminalPaneViewModel.Snapshot))
        {
            if (!ReferenceEquals(_pane.Manager?.Active, _appliedSession))
            {
                RequestResize();
            }

            InvalidateVisual();
        }
        else if (propertyName == nameof(TerminalPaneViewModel.Accessible))
        {
            ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
                ValuePatternIdentifiers.ValueProperty,
                null,
                TerminalAutomationPeer.ValueOf(_pane.Accessible));
        }
    }

    /// <summary>Creates the automation peer. The peer exposes the screen as a text value (doc 55, section 5.2).</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    private void Send(TerminalKey key)
    {
        TerminalSessionManager? manager = _pane.Manager;
        if (manager is null)
        {
            return;
        }

        Enqueue(() => manager.HandleKeyAsync(key));
    }

    private async Task PasteFromClipboardAsync()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        IClipboard? clipboard = top?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        string? text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(text))
        {
            Paste(text);
        }
    }

    private void Enqueue(Func<ValueTask> work)
    {
        lock (_inputGate)
        {
            _inputTail = _inputTail.ContinueWith(_ => RunAsync(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task RunAsync(Func<ValueTask> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Input can race a closing session. The status line is the place for the person to see why.
            Dispatcher.UIThread.Post(() => LastError = ex.Message);
        }
    }

    private static async ValueTask PasteAsync(TerminalSession session, string text) => await session.PasteAsync(text).ConfigureAwait(false);

    private void RequestResize()
    {
        CellSize cell = MeasureCell();
        (_pendingCols, _pendingRows) = GridFor(_arrangedSize, cell);
        if (ResizeDebounce <= TimeSpan.Zero)
        {
            ApplyResize();
            return;
        }

        _resizeTimer ??= new DispatcherTimer { Interval = ResizeDebounce };
        _resizeTimer.Interval = ResizeDebounce;
        _resizeTimer.Tick -= OnResizeTick;
        _resizeTimer.Tick += OnResizeTick;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();
        ApplyResize();
    }

    private void ApplyResize()
    {
        TerminalSession? session = _pane.Manager?.Active;
        if (session is null || _arrangedSize.Width <= 0 || _arrangedSize.Height <= 0)
        {
            return;
        }

        if (ReferenceEquals(session, _appliedSession) && _pendingCols == _appliedCols && _pendingRows == _appliedRows)
        {
            return;
        }

        _appliedSession = session;
        _appliedCols = _pendingCols;
        _appliedRows = _pendingRows;
        int cols = _pendingCols;
        int rows = _pendingRows;
        Enqueue(() => session.ResizeAsync(cols, rows));
    }

    private static void DrawRow(DrawingContext context, CellSize cell, TerminalPalette palette, TerminalPaneSnapshot snap, int row, int cols)
    {
        double y = row * cell.Height;
        var clusters = Clusters(snap.Rows[row]).Where(c => c.Column < cols).ToList();

        // Backgrounds: runs of one colour become one rectangle. The default background is already painted.
        Color? runColour = null;
        int runStart = 0;
        int runEnd = 0;
        foreach (TerminalCluster c in clusters)
        {
            (_, Color bg) = ResolveCellColors(c.Style, palette, IsSelected(snap, row, c.Column));
            bool adjacent = runColour is not null && c.Column == runEnd;
            if (runColour is { } current && (bg != current || !adjacent))
            {
                context.DrawRectangle(Brush(current), null, new Rect(runStart * cell.Width, y, (runEnd - runStart) * cell.Width, cell.Height));
                runColour = null;
            }

            if (bg == palette.Background)
            {
                continue;
            }

            if (runColour is null)
            {
                runColour = bg;
                runStart = c.Column;
            }

            runEnd = c.Column + c.Width;
        }

        if (runColour is { } last)
        {
            context.DrawRectangle(Brush(last), null, new Rect(runStart * cell.Width, y, (runEnd - runStart) * cell.Width, cell.Height));
        }

        // Text: narrow characters that share a colour and attributes are drawn as one string; wide ones alone.
        const CellAttributes textAttributes = CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Underline | CellAttributes.Strikethrough;
        var group = new StringBuilder();
        int groupStart = 0;
        int groupColumns = 0;
        Color groupFg = default;
        CellAttributes groupAttr = CellAttributes.None;
        foreach (TerminalCluster c in clusters)
        {
            (Color fg, _) = ResolveCellColors(c.Style, palette, IsSelected(snap, row, c.Column));
            CellAttributes attr = c.Style.Attributes & textAttributes;
            if (c.Width != 1)
            {
                FlushGroup();
                DrawText(context, cell, c.Column, y, c.Text, fg, attr, cell.Width * c.Width);
                continue;
            }

            if (group.Length > 0 && (fg != groupFg || attr != groupAttr))
            {
                FlushGroup();
            }

            if (group.Length == 0)
            {
                groupStart = c.Column;
                groupFg = fg;
                groupAttr = attr;
            }

            group.Append(c.Text);
            groupColumns++;
        }

        FlushGroup();

        void FlushGroup()
        {
            if (group.Length > 0)
            {
                DrawText(context, cell, groupStart, y, group.ToString(), groupFg, groupAttr, cell.Width * groupColumns);
            }

            group.Clear();
            groupColumns = 0;
        }
    }

    private static void DrawText(DrawingContext context, CellSize cell, int column, double y, string text, Color fg, CellAttributes attributes, double width)
    {
        if (text.Length == 0 || text.Trim().Length == 0)
        {
            return;
        }

        bool bold = attributes.HasFlag(CellAttributes.Bold);
        bool italic = attributes.HasFlag(CellAttributes.Italic);
        var typeface = new Typeface(cell.Family, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, cell.EmSize, Brush(fg));
        double x = column * cell.Width;
        context.DrawText(formatted, new Point(x, y));

        if (attributes.HasFlag(CellAttributes.Underline))
        {
            double lineY = y + cell.Height - 1;
            context.DrawLine(new Pen(Brush(fg), 1), new Point(x, lineY), new Point(x + width, lineY));
        }

        if (attributes.HasFlag(CellAttributes.Strikethrough))
        {
            double lineY = y + (cell.Height / 2);
            context.DrawLine(new Pen(Brush(fg), 1), new Point(x, lineY), new Point(x + width, lineY));
        }
    }

    private static void DrawCursor(DrawingContext context, CellSize cell, TerminalPalette palette, TerminalPaneSnapshot snap, int drawRows, int cols)
    {
        bool onScreen = snap.HasSession && snap.CursorRow >= 0 && snap.CursorRow < drawRows;
        if (!onScreen || (!snap.CursorVisible && snap.ModeName == "INSERT"))
        {
            return;
        }

        double x = Math.Min(snap.CursorColumn, cols - 1) * cell.Width;
        double y = snap.CursorRow * cell.Height;
        if (snap.ModeName == "INSERT")
        {
            context.DrawRectangle(Brush(palette.Cursor), null, new Rect(x, y, 2, cell.Height));
            return;
        }

        context.DrawRectangle(Brush(palette.Cursor), null, new Rect(x, y, cell.Width, cell.Height));
        TerminalCluster? under = Clusters(snap.Rows[snap.CursorRow]).FirstOrDefault(c => c.Column == snap.CursorColumn);
        if (under is { } cluster)
        {
            DrawText(context, cell, snap.CursorColumn, y, cluster.Text, palette.Background, CellAttributes.None, cell.Width * cluster.Width);
        }
    }

    private void DrawStatusRow(DrawingContext context, CellSize cell, TerminalPalette palette, TerminalPaneSnapshot snap, int rows, int cols)
    {
        double y = rows * cell.Height;
        if (y + cell.Height > Bounds.Height + 0.5)
        {
            return;
        }

        context.DrawRectangle(Brush(palette.StatusBackground), null, new Rect(0, y, Bounds.Width, cell.Height));
        string text = snap.ModeName == "COMMAND" ? snap.CommandLine : StatusText(snap);
        if (text.Length > cols)
        {
            text = text[..cols];
        }

        DrawText(context, cell, 0, y, text, palette.Foreground, CellAttributes.None, cell.Width * text.Length);

        if (snap.ModeName == "COMMAND")
        {
            double x = Math.Min(text.Length, cols - 1) * cell.Width;
            context.DrawRectangle(Brush(palette.Cursor), null, new Rect(x, y, cell.Width, cell.Height));
        }
    }

    /// <summary>The status line text: mode, status, and the replay and read-only markers.</summary>
    internal static string StatusText(TerminalPaneSnapshot snap)
    {
        var text = new StringBuilder($" {snap.ModeName} | {snap.StatusLine}");
        if (snap.IsReplaying)
        {
            text.Append(" [replay]");
        }

        if (snap.IsReadOnly)
        {
            text.Append(" [read-only]");
        }

        return text.ToString();
    }

    private static bool IsSelected(TerminalPaneSnapshot snap, int row, int col)
    {
        if (snap.ModeName is not ("VISUAL" or "V-LINE") || snap.CursorRow < 0)
        {
            return false;
        }

        int ar = snap.SelectionAnchorRow;
        int ac = snap.SelectionAnchorColumn;
        int cr = snap.CursorRow;
        int cc = snap.CursorColumn;
        bool anchorFirst = ar < cr || (ar == cr && ac <= cc);
        (int sr, int sc, int er, int ec) = anchorFirst ? (ar, ac, cr, cc) : (cr, cc, ar, ac);
        if (row < sr || row > er)
        {
            return false;
        }

        if (snap.ModeName == "V-LINE")
        {
            return true;
        }

        return !((row == sr && col < sc) || (row == er && col > ec));
    }

    /// <summary>Cell size of the theme font. Width is measured from a wide-enough sample so the grid is not too narrow.</summary>
    private static CellSize MeasureCell(FontFamily family)
    {
        double size = ThemeSize();
        var typeface = new Typeface(family);
        var sample = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black);
        return new CellSize(family, size, sample.WidthIncludingTrailingWhitespace, sample.Height);
    }

    private static FontFamily ThemeFont()
    {
        object? value = ThemeResources.Find(_fontFamilyToken);
        return value switch
        {
            FontFamily family => family,
            string name when name.Length > 0 => new FontFamily(name),
            _ => new FontFamily(_fallbackFontFamily),
        };
    }

    private static double ThemeSize()
    {
        object? value = ThemeResources.Find(_fontSizeToken);
        return value is double size && size > 0 ? size : _fallbackFontSize;
    }

    private static SolidColorBrush Brush(Color color) => new SolidColorBrush(color);
}

/// <summary>Cell size of the terminal font.</summary>
/// <param name="Family">The font family the cells use.</param>
/// <param name="EmSize">Font size in device-independent pixels.</param>
/// <param name="Width">Width of one column.</param>
/// <param name="Height">Height of one row.</param>
public sealed record CellSize(FontFamily Family, double EmSize, double Width, double Height);

/// <summary>One cell or wide character as drawn: its first column, its width in columns, its text and its style.</summary>
/// <param name="Column">First column, 0-based.</param>
/// <param name="Width">Columns it occupies: 1 or 2.</param>
/// <param name="Text">The cell text.</param>
/// <param name="Style">The pen state it was written with.</param>
public readonly record struct TerminalCluster(int Column, int Width, string Text, CellStyle Style);
