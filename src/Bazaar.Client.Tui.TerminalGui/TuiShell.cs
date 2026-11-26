using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>What a shell is told at creation. The defaults are the doc 58 defaults.</summary>
/// <param name="BellEnabled">Whether a serious alert rings the terminal bell once (doc 58 section 3a; the operator can turn it off in settings).</param>
/// <param name="Bell">Replaces the bell, for tests; null rings the terminal's own.</param>
public sealed record ShellOptions(bool BellEnabled = true, Action? Bell = null);

/// <summary>
/// The full-screen shell of the server TUI (doc 58 sections 3a and 4) on Terminal.Gui: a menu bar moved with arrow keys and Enter
/// (with the optional letter shortcuts), an alert banner slot, the content area for the screens, a status line and a type-to-search
/// box opened with "/". The menu behaviour is <see cref="MenuBarState"/>, shared with the console path, so this class only draws
/// it and translates keys. The look is the one fixed dark look (<see cref="FixedLook"/>), never themeable.
/// </summary>
public sealed class TuiShell : Runnable
{
    private const string _defaultStatus = "Arrows move, Enter chooses, / searches, Ctrl+C quits";

    private readonly IApplication _application;
    private readonly ShellOptions _options;
    private readonly MenuBarState _menu;
    private readonly MenuBarView _bar;
    private readonly Label _banner;
    private readonly View _content;
    private readonly Label _statusLine;
    private readonly ListOverlayView _overlay;
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    private string _status = _defaultStatus;
    private bool _searchActive;
    private string _query = "";
    private IReadOnlyList<string> _results = [];
    private int _resultIndex;

    public TuiShell(IApplication application, IReadOnlyList<MenuModel> menus, ShellOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(menus);
        _application = application;
        _options = options ?? new ShellOptions();
        _menu = new MenuBarState(menus);

        foreach (var menu in menus)
        {
            _titles[menu.Id] = menu.Title;
            foreach (var item in menu.Items)
            {
                _titles[item.Id] = $"{menu.Title} > {item.Title}";
            }
        }

        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        SetScheme(FixedLook.Body);

        _bar = new MenuBarView(_menu);
        _banner = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 0, Visible = false };
        _content = new View { X = 0, Y = Pos.Bottom(_banner), Width = Dim.Fill(), Height = Dim.Fill(1) };
        _statusLine = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = _status };
        _statusLine.SetScheme(FixedLook.Bar);
        _overlay = new ListOverlayView();

        // The overlay is added last so it draws over the banner and the content.
        Add(_bar, _banner, _content, _statusLine, _overlay);
        KeyDown += OnShellKeyDown;
    }

    /// <summary>The menu bar's state, for reading which menu is highlighted or open.</summary>
    public MenuBarState Menu => _menu;

    /// <summary>The area between the banner and the status line, where the screens put their views.</summary>
    public View Content => _content;

    /// <summary>The banner now showing, or null.</summary>
    public ShellAlert? Alert { get; private set; }

    /// <summary>True while the search box is open.</summary>
    public bool SearchActive => _searchActive;

    /// <summary>The search text typed so far.</summary>
    public string SearchQuery => _query;

    /// <summary>The line drawn at the bottom: the status text, or the search box while it is open.</summary>
    public string StatusText => _statusLine.Text;

    /// <summary>Raised with the id of the menu or item chosen (from the bar, a shortcut or the search box).</summary>
    public event Action<string>? Chosen;

    /// <summary>Runs the shell until <see cref="Stop"/> or Ctrl+C.</summary>
    public void RunShell() => _application.Run(this);

    /// <summary>Ends <see cref="RunShell"/>.</summary>
    public void Stop() => _application.RequestStop(this);

    /// <summary>Replaces the status text. Safe from any thread.</summary>
    public void SetStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _application.Invoke(() =>
        {
            _status = text.Length == 0 ? _defaultStatus : text;
            Refresh();
        });
    }

    /// <summary>
    /// Shows a banner. A serious one rings the bell once (when enabled) and stays until <see cref="AcknowledgeAlert"/>; a health win
    /// stays until dismissed the same way. Safe from any thread.
    /// </summary>
    public void ShowAlert(ShellAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        _application.Invoke(() =>
        {
            var isNew = Alert != alert;
            Alert = alert;
            if (isNew && alert.Kind == ShellAlertKind.Serious && _options.BellEnabled)
            {
                RingBell();
            }

            Refresh();
        });
    }

    /// <summary>Clears the banner. Safe from any thread.</summary>
    public void AcknowledgeAlert() => _application.Invoke(() =>
    {
        Alert = null;
        Refresh();
    });

    private void RingBell()
    {
        if (_options.Bell is { } bell)
        {
            bell();
            return;
        }

        _application.Driver?.WriteRaw("\a");
    }

    private void OnShellKeyDown(object? sender, Key key)
    {
        var translated = KeyTranslator.Translate(key);
        if (translated is not { } terminalKey)
        {
            return;
        }

        key.Handled = true;
        if (terminalKey.Kind == TerminalKeyKind.Cancel)
        {
            Stop();
            return;
        }

        if (_searchActive)
        {
            HandleSearchKey(terminalKey);
        }
        else if (terminalKey is { Kind: TerminalKeyKind.Character, Value: '/' })
        {
            OpenSearch();
        }
        else if (terminalKey.Kind == TerminalKeyKind.Escape && !_menu.IsOpen && Alert is not null)
        {
            Alert = null;
        }
        else if (_menu.Handle(terminalKey) is { } chosen)
        {
            Chosen?.Invoke(chosen);
        }

        Refresh();
    }

    private void OpenSearch()
    {
        _searchActive = true;
        _query = "";
        _results = [];
        _resultIndex = 0;
    }

    private void HandleSearchKey(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Escape:
                _searchActive = false;
                return;
            case TerminalKeyKind.Enter:
                _searchActive = false;
                if (_results.Count > 0)
                {
                    Chosen?.Invoke(_results[_resultIndex]);
                }

                return;
            case TerminalKeyKind.Up when _results.Count > 0:
                _resultIndex = (_resultIndex - 1 + _results.Count) % _results.Count;
                return;
            case TerminalKeyKind.Down when _results.Count > 0:
                _resultIndex = (_resultIndex + 1) % _results.Count;
                return;
            case TerminalKeyKind.Backspace when _query.Length > 0:
                _query = _query[..^1];
                break;
            case TerminalKeyKind.Character when !char.IsControl(key.Value):
                _query += key.Value;
                break;
            default:
                return;
        }

        _results = _menu.Search(_query);
        _resultIndex = 0;
    }

    private void Refresh()
    {
        RefreshBanner();
        RefreshOverlay();
        _statusLine.Text = _searchActive ? $"Search: {_query}_   (Up and Down pick, Enter chooses, Esc closes)" : _status;
        _bar.SetNeedsDraw();
        SetNeedsDraw();
    }

    private void RefreshBanner()
    {
        if (Alert is not { } alert)
        {
            _banner.Visible = false;
            _banner.Height = 0;
            _banner.Text = "";
            return;
        }

        // The word comes first so the kind reads without colour.
        var (word, hint, scheme) = alert.Kind == ShellAlertKind.Serious
            ? ("SERIOUS", "Esc acknowledges", FixedLook.Serious)
            : ("OK", "Esc dismisses", FixedLook.Win);
        _banner.SetScheme(scheme);
        _banner.Text = $" {word}: {alert.Text}   [{hint}]";
        _banner.Height = 1;
        _banner.Visible = true;
    }

    private void RefreshOverlay()
    {
        if (_searchActive)
        {
            var lines = _results.Select((id, index) => new OverlayLine(_titles.GetValueOrDefault(id, id), null, index == _resultIndex)).ToList();
            if (lines.Count == 0 && _query.Length > 0)
            {
                lines.Add(new OverlayLine("No match", null, false));
            }

            _overlay.Visible = lines.Count > 0;
            _overlay.SetLines(0, lines);
            return;
        }

        if (_menu.IsOpen)
        {
            var items = _menu.Menus[_menu.MenuIndex].Items;
            var lines = items
                .Select((item, index) => new OverlayLine(item.Title, _menu.ShortcutsEnabled ? item.Shortcut : null, index == _menu.ItemIndex))
                .ToList();
            _overlay.SetLines(_bar.ColumnOf(_menu.MenuIndex), lines);
            _overlay.Visible = true;
            return;
        }

        _overlay.Visible = false;
    }
}
