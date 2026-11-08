using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// The phone's root view. Folded shows a single-column pager, one pane per page; unfolded shows two panes side
/// by side per page (doc 29). The pane list comes from the <see cref="PaneRegistry"/> in registration order.
/// Pane bodies are placeholders that name the pane until the GC and Kanban panes plug in.
/// </summary>
public sealed class PhoneShell : UserControl
{
    private readonly PaneRegistry _registry;
    private readonly Carousel _pager = new();
    private IReadOnlyList<IReadOnlyList<string>> _pages = [];

    /// <summary>A shell with no panes registered.</summary>
    public PhoneShell()
        : this(new PaneRegistry())
    {
    }

    public PhoneShell(PaneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;

        var title = new TextBlock { Text = "Bazaar", FontSize = 22, Margin = new Thickness(16, 16, 16, 8), FontWeight = FontWeight.SemiBold };
        DockPanel.SetDock(title, Dock.Top);

        // The pager is the last child, so DockPanel gives it the remaining height.
        Content = new DockPanel { Children = { title, _pager } };
        BuildPages();
    }

    public LayoutVariant Layout { get; private set; } = LayoutVariant.Folded;

    /// <summary>The pages currently shown, each a list of pane kinds in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Pages => _pages;

    /// <summary>Index of the page on screen, or -1 when there are no pages.</summary>
    public int SelectedPage => _pages.Count == 0 ? -1 : _pager.SelectedIndex;

    /// <summary>Shows a page by index. Out-of-range indexes are ignored.</summary>
    public void GoToPage(int index)
    {
        if (index >= 0 && index < _pages.Count)
        {
            _pager.SelectedIndex = index;
        }
    }

    /// <summary>Switches variants locally when the posture or width changes. No round trip (doc 29).</summary>
    public LayoutVariant ApplyPosture(PostureKind posture, HalfOpenLayoutPolicy halfOpen, double widthDp)
    {
        SetLayout(PostureLayout.Select(posture, halfOpen, widthDp));
        return Layout;
    }

    private void SetLayout(LayoutVariant variant)
    {
        if (variant == Layout)
        {
            return;
        }

        // Keep the first visible pane in view across the switch, so a fold does not jump the user to another pane.
        var firstPane = Math.Max(_pager.SelectedIndex, 0) * PanePages.PanesPerPage(Layout);
        Layout = variant;
        BuildPages();
        _pager.SelectedIndex = Math.Min(firstPane / PanePages.PanesPerPage(variant), Math.Max(_pages.Count - 1, 0));
    }

    private void BuildPages()
    {
        _pages = PanePages.Paginate(Layout, _registry.Kinds);
        _pager.Items.Clear();

        if (_pages.Count == 0)
        {
            _pager.Items.Add(new TextBlock { Text = "No panes yet", Margin = new Thickness(16), FontSize = 16 });
            return;
        }

        foreach (var page in _pages)
        {
            _pager.Items.Add(PageView(page));
        }

        _pager.SelectedIndex = 0;
    }

    private static Grid PageView(IReadOnlyList<string> kinds)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(8) };
        for (var i = 0; i < kinds.Count; i++)
        {
            var placeholder = new Border
            {
                Padding = new Thickness(12),
                Margin = new Thickness(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = kinds[i], TextWrapping = TextWrapping.Wrap, FontSize = 16 },
            };
            Grid.SetColumn(placeholder, i);
            grid.Children.Add(placeholder);
        }

        return grid;
    }
}
