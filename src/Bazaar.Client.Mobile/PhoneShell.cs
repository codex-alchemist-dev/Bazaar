using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Mobile;

/// <summary>
/// The phone's root view. Folded shows a single-column pager, one pane per page; unfolded shows two panes side
/// by side per page (doc 29). The pane list comes from the <see cref="PaneRegistry"/> in registration order.
/// Pane bodies are placeholders that name the pane until the GC and Kanban panes plug in.
/// </summary>
public sealed class PhoneShell : UserControl
{
    private readonly PaneRegistry _registry;
    private readonly Func<string, Control?>? _paneBody;
    private readonly Carousel _pager = new();
    private readonly DockPanel _root;
    private readonly PhoneDrawer? _drawer;
    private IReadOnlyList<IReadOnlyList<string>> _pages = [];
    private IReadOnlyList<string>? _profileKinds;

    /// <param name="registry">The panes to page through, in order.</param>
    /// <param name="paneBody">Returns the real view for a pane kind, or null to show the kind-name placeholder.</param>
    /// <param name="drawer">
    /// The pane shown in the bottom drawer of the unfolded layout, for example the read-only Constellation (doc 29).
    /// Null shows no drawer. The drawer's body comes from the spec, else from <paramref name="paneBody"/>.
    /// </param>
    public PhoneShell(PaneRegistry registry, Func<string, Control?>? paneBody = null, PhoneDrawerSpec? drawer = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _paneBody = paneBody;

        var title = new TextBlock { Text = "Bazaar", FontSize = 22, Margin = new Thickness(16, 16, 16, 8), FontWeight = FontWeight.SemiBold };
        DockPanel.SetDock(title, Dock.Top);

        _root = new DockPanel { Children = { title } };
        if (drawer is not null)
        {
            var body = drawer.Body?.Invoke() ?? paneBody?.Invoke(drawer.Kind) ?? new TextBlock { Text = drawer.Kind, TextWrapping = TextWrapping.Wrap, FontSize = 16 };
            _drawer = new PhoneDrawer(drawer, body);
            DockPanel.SetDock(_drawer, Dock.Bottom);
        }

        // The pager is the last child, so DockPanel gives it the remaining height.
        _root.Children.Add(_pager);
        Content = _root;
        BuildPages();
        UpdateDrawer();
    }

    /// <summary>The bottom drawer, or null when the shell was built without one. It only shows in the unfolded layout.</summary>
    public PhoneDrawer? Drawer => _drawer;

    /// <summary>True while the drawer is part of the shell's view, which is the unfolded layout with a drawer configured.</summary>
    public bool IsDrawerShown => _drawer is not null && _root.Children.Contains(_drawer);

    public LayoutVariant Layout { get; private set; } = LayoutVariant.Folded;

    /// <summary>
    /// Revision of the server profile on screen, or null while the shell shows the registry order (no profile applied yet).
    /// </summary>
    public long? ProfileRevision { get; private set; }

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

    /// <summary>
    /// Shows the cached profile, if one is stored, so a cold start paints before the network answers (doc 29, doc 27).
    /// A missing or corrupt cache leaves the registry order on screen.
    /// </summary>
    /// <returns>True when a cached profile was shown.</returns>
    public bool ShowCachedProfile(IProfileCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var cached = cache.Load();
        if (cached is null)
        {
            return false;
        }

        ApplyProfile(cached.Profile);
        return true;
    }

    /// <summary>
    /// Replaces the pages with a server profile. A fresh profile replaces the cached one. The first visible pane stays
    /// on screen when the new profile still has a pane at that position.
    /// </summary>
    public void ApplyProfile(PhoneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var firstPane = FirstVisiblePane();
        _profileKinds = profile.PaneKinds.ToArray();
        ProfileRevision = profile.Revision;
        BuildPages();
        SelectPageContaining(firstPane);
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
        var firstPane = FirstVisiblePane();
        Layout = variant;
        BuildPages();
        SelectPageContaining(firstPane);
        UpdateDrawer();
    }

    /// <summary>Shows the drawer in the unfolded layout and hides it when folded. The drawer object, and so its open state, is kept.</summary>
    private void UpdateDrawer()
    {
        if (_drawer is null)
        {
            return;
        }

        var shown = _root.Children.Contains(_drawer);
        if (Layout == LayoutVariant.Unfolded && !shown)
        {
            // Before the pager, which must stay last so it takes the remaining height.
            _root.Children.Insert(_root.Children.Count - 1, _drawer);
        }
        else if (Layout != LayoutVariant.Unfolded && shown)
        {
            _root.Children.Remove(_drawer);
        }
    }

    /// <summary>Index into the pane list of the first pane on the selected page.</summary>
    private int FirstVisiblePane() => Math.Max(_pager.SelectedIndex, 0) * PanePages.PanesPerPage(Layout);

    /// <summary>Selects the page that holds the pane at <paramref name="paneIndex"/>, or the last page if it is past the end.</summary>
    private void SelectPageContaining(int paneIndex) =>
        _pager.SelectedIndex = Math.Min(paneIndex / PanePages.PanesPerPage(Layout), Math.Max(_pages.Count - 1, 0));

    private void BuildPages()
    {
        // The applied profile's pane order wins; the registry order is what shows before any profile.
        _pages = PanePages.Paginate(Layout, _profileKinds ?? _registry.Kinds);
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

    private Grid PageView(IReadOnlyList<string> kinds)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(8) };
        for (var i = 0; i < kinds.Count; i++)
        {
            Control body = _paneBody?.Invoke(kinds[i]) ?? new TextBlock { Text = kinds[i], TextWrapping = TextWrapping.Wrap, FontSize = 16 };
            var placeholder = new Border
            {
                Padding = new Thickness(12),
                Margin = new Thickness(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                CornerRadius = new CornerRadius(4),
                Child = body,
            };
            Grid.SetColumn(placeholder, i);
            grid.Children.Add(placeholder);
        }

        return grid;
    }
}
