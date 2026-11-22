using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// The native diff hunk surface (doc 21, Client review and merge: Android has no Monaco, so the diff is drawn natively). It is a
/// plain stack of monospace lines: a header per hunk, added lines in green, removed in red, context unmarked. Read only.
/// </summary>
public sealed class PullRequestDiffSurface : ScrollViewer
{
    private static readonly IBrush _added = new SolidColorBrush(Color.FromArgb(0x33, 0x2E, 0xA0, 0x43));
    private static readonly IBrush _removed = new SolidColorBrush(Color.FromArgb(0x33, 0xD7, 0x3A, 0x49));
    private static readonly IBrush _header = new SolidColorBrush(Color.FromRgb(0x6A, 0x73, 0x7D));

    private readonly StackPanel _lines = new() { Spacing = 0 };

    public PullRequestDiffSurface()
    {
        Name = "DiffSurface";
        Content = _lines;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    /// <summary>Number of lines drawn, for tests and for the host's status.</summary>
    public int LineCount => _lines.Children.Count;

    /// <summary>Replaces the drawn lines.</summary>
    public void Render(IEnumerable<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _lines.Children.Clear();
        foreach (var line in lines)
        {
            _lines.Children.Add(new TextBlock
            {
                Text = line.Text,
                FontFamily = new FontFamily("monospace"),
                Background = Brush(line.Kind),
                Foreground = line.Kind == DiffLineKind.Header ? _header : null,
                Margin = new Thickness(0, 1),
            });
        }
    }

    private static IBrush? Brush(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => _added,
        DiffLineKind.Removed => _removed,
        _ => null,
    };
}
