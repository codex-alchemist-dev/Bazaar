using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>
/// A side-by-side diff (doc 57 §2): the original on the left and the modified text on the right in two read-only
/// <see cref="TextEditor"/>s. Changed blocks are padded with empty filler lines so both sides have the same line count,
/// removed and added lines get the theme's backgrounds, and the two scroll offsets stay equal. UI thread only.
/// </summary>
public sealed class DiffView : Grid
{
    private readonly LineBackground _leftBackground = new();
    private readonly LineBackground _rightBackground = new();
    private bool _syncing;
    private DiffColors _colors = DiffColors.Default;

    public DiffView()
    {
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2)));
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Left = CreateEditor(_leftBackground);
        Right = CreateEditor(_rightBackground);
        SetColumn(Left, 0);
        SetColumn(Right, 2);
        Children.Add(Left);
        Children.Add(Right);
        Left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Mirror(Left, Right);
        Right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Mirror(Right, Left);
        ApplyColors(DiffColors.Default);
    }

    /// <summary>The original side.</summary>
    public TextEditor Left { get; }

    /// <summary>The modified side.</summary>
    public TextEditor Right { get; }

    /// <summary>The aligned rows shown now.</summary>
    public IReadOnlyList<DiffRow> Rows { get; private set; } = [];

    /// <summary>The colours in use.</summary>
    public DiffColors Colors => _colors;

    /// <summary>The kind of the row at a zero-based line index, the same on both sides.</summary>
    public DiffRowKind KindAt(int lineIndex) => Rows[lineIndex].Kind;

    /// <summary>Shows a diff. The original is left, the modified text is right.</summary>
    public void Show(string original, string modified, IHighlightingDefinition? highlighting)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        IReadOnlyList<DiffRow> rows = LineDiff.Compute(original, modified);
        Rows = rows;
        Left.Text = string.Join('\n', rows.Select(r => r.Left ?? string.Empty));
        Right.Text = string.Join('\n', rows.Select(r => r.Right ?? string.Empty));
        _leftBackground.Set(rows.Select(r => r.Left is null ? LineState.Filler : r.Kind is DiffRowKind.Equal ? LineState.Plain : LineState.Removed));
        _rightBackground.Set(rows.Select(r => r.Right is null ? LineState.Filler : r.Kind is DiffRowKind.Equal ? LineState.Plain : LineState.Added));
        Left.SyntaxHighlighting = highlighting;
        Right.SyntaxHighlighting = highlighting;
        Left.ScrollToHome();
        Right.ScrollToHome();
        Left.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        Right.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>Sets the diff line colours, the editor colours and the syntax of both sides.</summary>
    public void ApplyColors(DiffColors colors, EditorColors? editor = null, IHighlightingDefinition? highlighting = null)
    {
        ArgumentNullException.ThrowIfNull(colors);
        _colors = colors;
        _leftBackground.Colors = colors;
        _rightBackground.Colors = colors;
        foreach (TextEditor side in new[] { Left, Right })
        {
            if (editor?.Background is { } background)
            {
                side.Background = new SolidColorBrush(background);
            }

            if (editor?.Foreground is { } foreground)
            {
                side.Foreground = new SolidColorBrush(foreground);
                side.LineNumbersForeground = new SolidColorBrush(foreground, 0.5);
            }

            if (highlighting is not null)
            {
                side.SyntaxHighlighting = highlighting;
            }

            side.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
    }

    /// <summary>The scroll offset of the left side (the right side equals it after layout).</summary>
    public Vector LeftOffset => Left.TextArea.TextView.ScrollOffset;

    /// <summary>The scroll offset of the right side.</summary>
    public Vector RightOffset => Right.TextArea.TextView.ScrollOffset;

    /// <summary>Scrolls the left side, and the right side follows.</summary>
    public void ScrollTo(Vector offset)
    {
        SetOffset(Left, offset);
    }

    private static TextEditor CreateEditor(LineBackground background)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = new FontFamily("Cascadia Code,Consolas,DejaVu Sans Mono,Liberation Mono,monospace"),
        };
        editor.TextArea.TextView.BackgroundRenderers.Add(background);
        return editor;
    }

    private static void SetOffset(TextEditor editor, Vector offset) =>
        ((IScrollable)editor.TextArea.TextView).Offset = offset;

    private void Mirror(TextEditor from, TextEditor to)
    {
        Vector source = from.TextArea.TextView.ScrollOffset;
        if (_syncing || to.TextArea.TextView.ScrollOffset == source)
        {
            return;
        }

        _syncing = true;
        try
        {
            SetOffset(to, source);
        }
        finally
        {
            _syncing = false;
        }
    }

    private enum LineState
    {
        Plain,
        Removed,
        Added,
        Filler,
    }

    private sealed class LineBackground : IBackgroundRenderer
    {
        private LineState[] _states = [];

        public DiffColors Colors { get; set; } = DiffColors.Default;

        public KnownLayer Layer => KnownLayer.Background;

        public void Set(IEnumerable<LineState> states) => _states = states.ToArray();

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid)
            {
                return;
            }

            foreach (VisualLine visual in textView.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= _states.Length)
                {
                    continue;
                }

                Color? colour = _states[index] switch
                {
                    LineState.Removed => Colors.Removed,
                    LineState.Added => Colors.Added,
                    LineState.Filler => Colors.Filler,
                    _ => null,
                };
                if (colour is { } c)
                {
                    double y = visual.VisualTop - textView.ScrollOffset.Y;
                    drawingContext.FillRectangle(new SolidColorBrush(c), new Rect(0, y, textView.Bounds.Width, visual.Height));
                }
            }
        }
    }
}
