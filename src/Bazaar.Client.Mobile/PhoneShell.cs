using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.BackStack.Abstractions;
using Bazaar.Client.Gestures;
using Bazaar.Client.Gestures.Avalonia;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Mobile;

/// <summary>
/// The phone's root view. Folded shows a single-column pager, one pane per page; unfolded shows two panes side
/// by side per page (doc 29). The pane list comes from the <see cref="PaneRegistry"/> in registration order.
/// Pane bodies are placeholders that name the pane until the GC and Kanban panes plug in.
/// </summary>
public sealed class PhoneShell : UserControl, IDisposable
{
    private readonly PaneRegistry _registry;
    private readonly Func<string, Control?>? _paneBody;
    private readonly Carousel _pager = new();
    private readonly SpringTranslateAnimator _settle;
    private IReadOnlyList<IReadOnlyList<string>> _pages = [];
    private IReadOnlyList<string>? _profileKinds;
    private readonly IScreenHistory? _history;
    private readonly ScreenStackPresenter? _presenter;

    /// <param name="registry">The panes to page through, in order.</param>
    /// <param name="paneBody">Returns the real view for a pane kind, or null to show the kind-name placeholder.</param>
    /// <param name="gestures">The swipe and hold thresholds. Null uses the doc 93 defaults.</param>
    /// <param name="radialSectors">The radial menu's sectors, in order. Null uses the doc 29 default seven.</param>
    /// <param name="motion">The motion level and tokens; null uses the phone defaults. At the reduced level swipes settle without animation.</param>
    /// <param name="frames">Animation frames for the spring settle; null uses the pager's top level.</param>
    /// <param name="history">The back stack. With one, screens pushed on it show over the pager and system back pops them with the predictive transition (doc 93). Null keeps the shell a plain pager.</param>
    /// <param name="screenView">Returns the view for a pushed stack entry, or null for a placeholder. Used only with <paramref name="history"/>.</param>
    /// <param name="backSource">The system back gesture. Null wires no system back; <see cref="GoBack"/> still pops.</param>
    /// <param name="backSettings">Predictive back thresholds; null uses the doc 93 defaults.</param>
    public PhoneShell(PaneRegistry registry, Func<string, Control?>? paneBody = null, GestureOptions? gestures = null, IReadOnlyList<string>? radialSectors = null, MotionSettings? motion = null, IFrameDriver? frames = null, IScreenHistory? history = null, Func<ScreenEntry, Control?>? screenView = null, IBackGestureSource? backSource = null, GestureSettings? backSettings = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _paneBody = paneBody;

        var title = new TextBlock { Text = "Bazaar", FontSize = 22, Margin = new Thickness(16, 16, 16, 8), FontWeight = FontWeight.SemiBold };
        DockPanel.SetDock(title, Dock.Top);

        // The gesture layer wraps the pager and is the last child, so DockPanel gives it the remaining height.
        Gestures = new PhoneGestureLayer(_pager, radialSectors, gestures);
        // The page follows the finger, then settles with the Fluid spring from the release velocity (doc 93, package 4).
        var frameDriver = frames ?? new TopLevelFrameDriver(_pager);
        var motionSettings = motion ?? new MotionSettings(MotionPlatform.Phone);
        _settle = new SpringTranslateAnimator(_pager, frameDriver, motionSettings);
        Gestures.SwipeProgressed += (_, dx) => _settle.Follow(dx);
        Gestures.SwipeCancelled += (_, _) => _settle.Cancel();
        Gestures.SwipeCommitted += (_, direction) =>
        {
            var next = direction == SwipeDirection.Next;
            if (next ? GoToNext() : GoToPrevious())
            {
                // The new page enters from where the old one left.
                _settle.Commit(_pager.Bounds.Width, next ? 1 : -1);
            }
            else
            {
                _settle.Cancel();
            }
        };
        Gestures.DictationChanged += (_, on) => DictationChanged?.Invoke(this, on);
        Gestures.RadialReleased += (_, release) => RadialReleased?.Invoke(this, release);
        var body = new Grid { Children = { Gestures } };
        Content = new DockPanel { Children = { title, body } };
        BuildPages();

        if (history is not null)
        {
            _history = history;
            _presenter = new ScreenStackPresenter(body, Gestures, history, screenView ?? (_ => null), backSource, frameDriver, motionSettings, backSettings);
            history.Changed += (_, _) => CanGoBackChanged?.Invoke(this, history.CanPop);
        }
    }

    /// <summary>The swipe and hold layer over the pager. Heads and tests reach the radial overlay through it.</summary>
    public PhoneGestureLayer Gestures { get; }

    /// <summary>Dictation should start (true) or stop (false) as the finger moves in the radial menu.</summary>
    public event EventHandler<bool>? DictationChanged;

    /// <summary>The radial menu was released: a sector to open, a transcript to place in the composer, or a cancel.</summary>
    public event EventHandler<RadialRelease>? RadialReleased;

    /// <summary>The back stack, or null when the shell was built without one.</summary>
    public IScreenHistory? History => _history;

    /// <summary>The predictive back host over the top two screens, or null at the root (or without a stack).</summary>
    public PredictiveBackHost? BackHost => _presenter?.BackHost;

    /// <summary>The view on top: the pager at the root, otherwise the pushed screen's view.</summary>
    public Control CurrentScreenView => _presenter?.CurrentView ?? Gestures;

    /// <summary>True when back would pop a screen. The head enables its system back callback only while this is true, so back at the root leaves the app.</summary>
    public bool CanGoBack => _history?.CanPop ?? false;

    /// <summary>Raised with the new <see cref="CanGoBack"/> after the stack changes.</summary>
    public event EventHandler<bool>? CanGoBackChanged;

    /// <summary>Pops the top screen at once (no transition). Returns false at the root. Also the route for the accessibility back action.</summary>
    public bool GoBack() => _history?.Pop() ?? false;

    public LayoutVariant Layout { get; private set; } = LayoutVariant.Folded;

    /// <summary>
    /// Revision of the server profile on screen, or null while the shell shows the registry order (no profile applied yet).
    /// </summary>
    public long? ProfileRevision { get; private set; }

    /// <summary>The pages currently shown, each a list of pane kinds in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Pages => _pages;

    /// <summary>Index of the page on screen, or -1 when there are no pages.</summary>
    public int SelectedPage => _pages.Count == 0 ? -1 : _pager.SelectedIndex;

    /// <summary>Shows the next page. Returns false on the last page. Also the route for the accessibility action "Next chat".</summary>
    public bool GoToNext() => Step(+1);

    /// <summary>Shows the previous page. Returns false on the first page. Also the route for the accessibility action "Previous chat".</summary>
    public bool GoToPrevious() => Step(-1);

    /// <summary>Stops listening to the back stack and the system back gesture.</summary>
    public void Dispose() => _presenter?.Dispose();

    private bool Step(int delta)
    {
        var target = _pager.SelectedIndex + delta;
        if (_pages.Count == 0 || target < 0 || target >= _pages.Count)
        {
            return false;
        }

        _pager.SelectedIndex = target;
        return true;
    }

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
