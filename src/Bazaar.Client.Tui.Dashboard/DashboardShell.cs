namespace Bazaar.Client.Tui.Dashboard;

/// <summary>One drawn frame: the lines (never more than the shell's rows) and whether the bell should ring with it.</summary>
public sealed record DashboardFrame(IReadOnlyList<string> Lines, bool Bell);

/// <summary>
/// The resident TUI as a view model with a text renderer (doc 58 sections 3 to 5), independent of any UI library: it reads keys and
/// draws lines through <see cref="ITerminal"/>, so the Terminal.Gui shell can replace the drawing later without touching the rules.
/// The menu bar comes from <see cref="DefaultMenus"/> and <see cref="MenuBarState"/> (arrow keys and Enter, optional letter shortcuts
/// and type-to-search with <c>/</c>). The default screen is the health overview. Alerts follow <see cref="AlertCenter"/>.
/// <para>Keys beyond the menu bar: <c>Tab</c> acknowledges the alert banner and dismisses the green banner; on the Logs screen
/// <c>[</c> and <c>]</c> lower and raise the level filter and <c>f</c> sets the module filter; Ctrl+C leaves. Data is read again on
/// every key; a timed refresh waits for the Terminal.Gui shell.</para>
/// </summary>
public sealed class DashboardShell
{
    public const int DefaultRows = 24;

    private readonly ITerminal _terminal;
    private readonly OpsPorts _ports;
    private readonly OpsSignIn _signIn;
    private readonly TuiStyle _style;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly int _rows;
    private int _lastDrawn;

    public DashboardShell(
        ITerminal terminal,
        OpsPorts ports,
        OpsSignIn signIn,
        TuiStyle style,
        TimeProvider? clock = null,
        TimeZoneInfo? zone = null,
        int rows = DefaultRows)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _signIn = signIn ?? throw new ArgumentNullException(nameof(signIn));
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _clock = clock ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _rows = Math.Max(10, rows);
    }

    public MenuBarState Menu { get; } = new(DefaultMenus.Create());

    public AlertCenter Alerts { get; } = new();

    public LogsFilterState LogsFilter { get; } = new();

    /// <summary>The screen shown now. The health overview is the default (owner card 2026-11-18).</summary>
    public string CurrentScreen { get; private set; } = DefaultMenus.Health;

    /// <summary>Reads the ports, updates the alerts and draws one frame.</summary>
    public async Task<DashboardFrame> RenderAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var width = Math.Max(40, _terminal.Width);
        var provisional = new ScreenContext(width, _rows, _style, _zone, now);

        var status = await SoftPort.TryAsync(_ports.Status, (source, token) => source.GetAsync(token), ct).ConfigureAwait(false);
        var kodo = await SoftPort.TryAsync(_ports.Kodo, (source, token) => source.GetAsync(token), ct).ConfigureAwait(false);
        var events = await OpsScreens.ReadEventsAsync(_ports, _rows, ct).ConfigureAwait(false) ?? [];
        var overview = HealthOverviewBuilder.Build(status, kodo, events, now, _zone);
        var bell = Alerts.Observe(overview.SeriousItems);
        Alerts.ObserveWins(events);

        var top = MenuLines(provisional);
        top.AddRange(BannerLines(provisional));
        var footer = FooterLine(width);
        var bodyRows = Math.Max(1, _rows - top.Count - 1);
        var context = provisional with { Rows = bodyRows };

        IReadOnlyList<string> body = CurrentScreen == DefaultMenus.Health
            ? HealthOverviewRenderer.Render(overview, context)
            : await OpsScreens.LoadAsync(CurrentScreen, _ports, context, LogsFilter, ct).ConfigureAwait(false);

        var lines = new List<string>(top);
        lines.AddRange(body.Take(bodyRows));
        lines.Add(footer);
        return new DashboardFrame(lines.Take(_rows).ToList(), bell);
    }

    /// <summary>Handles one key. Returns false when the user chose to leave.</summary>
    public bool HandleKey(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Cancel:
            case TerminalKeyKind.EndOfInput:
                return false;
            case TerminalKeyKind.Tab:
                Alerts.Acknowledge();
                Alerts.DismissWins();
                return true;
        }

        if (key is { Kind: TerminalKeyKind.Character, Value: '/' })
        {
            var query = new TerminalPromptWidgets(_terminal).TextEntry("Go to", null);
            var match = query is null ? [] : Menu.Search(query);
            if (match.Count > 0)
            {
                CurrentScreen = match[0];
            }

            return true;
        }

        if (CurrentScreen == DefaultMenus.Logs && !Menu.IsOpen && key.Kind == TerminalKeyKind.Character)
        {
            switch (key.Value)
            {
                case '[':
                    LogsFilter.Lower();
                    return true;
                case ']':
                    LogsFilter.Raise();
                    return true;
                case 'f' or 'F':
                    LogsFilter.Module = new TerminalPromptWidgets(_terminal).TextEntry("Module filter (empty for all)", LogsFilter.Module);
                    return true;
            }
        }

        if (Menu.Handle(key) is { } chosen)
        {
            CurrentScreen = chosen;
        }

        return true;
    }

    /// <summary>Signs in (owner on the box, or an admin code), then draws and reads keys until the user leaves. Returns the exit code: 0 on a normal exit, 1 when sign-in was abandoned, 2 when the terminal cannot read keys.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        if (!_terminal.SupportsKeys)
        {
            _terminal.WriteLine("The full-screen dashboard needs a terminal that reads single keys. Use --plain or the web admin panel.");
            return 2;
        }

        var session = await SignInLoopAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            return 1;
        }

        var open = session;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await _signIn.IsActiveAsync(session, ct).ConfigureAwait(false))
                {
                    Erase();
                    _terminal.WriteLine("Your session ended. Sign in again.");
                    var next = await SignInLoopAsync(ct).ConfigureAwait(false);
                    if (next is null)
                    {
                        return 1;
                    }

                    session = open = next;
                }

                var frame = await RenderAsync(ct).ConfigureAwait(false);
                Draw(frame);
                if (!HandleKey(_terminal.ReadKey()))
                {
                    break;
                }
            }
        }
        finally
        {
            await _signIn.SignOutAsync(open, CancellationToken.None).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<OpsSession?> SignInLoopAsync(CancellationToken ct)
    {
        var widgets = new TerminalPromptWidgets(_terminal);
        const string owner = "Owner on this box";
        const string code = "Admin code";
        while (!ct.IsCancellationRequested)
        {
            var pick = widgets.PickOne("Sign in to the Bazaar server dashboard", [owner, code], owner);
            if (pick is null)
            {
                return null;
            }

            OpsSession? session;
            if (pick == owner)
            {
                session = await _signIn.SignInOwnerAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // Read without echo; the code is never written to the screen, only handed to the redeemer.
                var typed = widgets.SecretEntry("Single-use admin code");
                if (typed is null)
                {
                    return null;
                }

                session = await _signIn.SignInWithCodeAsync(typed, ct).ConfigureAwait(false);
            }

            if (session is not null)
            {
                return session;
            }

            _terminal.WriteLine("Sign-in refused.");
        }

        return null;
    }

    private void Draw(DashboardFrame frame)
    {
        Erase();
        if (frame.Bell)
        {
            _terminal.Write("\a");
        }

        foreach (var line in frame.Lines)
        {
            _terminal.WriteLine(line);
        }

        _lastDrawn = frame.Lines.Count;
    }

    private void Erase()
    {
        if (_lastDrawn > 0)
        {
            _terminal.EraseLinesAbove(_lastDrawn);
            _lastDrawn = 0;
        }
    }

    private List<string> MenuLines(ScreenContext context)
    {
        var parts = new List<string>();
        for (var index = 0; index < Menu.Menus.Count; index++)
        {
            var menu = Menu.Menus[index];
            var title = Shortcut(menu.Title, menu.Shortcut, context.Style);
            parts.Add(index == Menu.MenuIndex ? Ansi.Reverse(context.Style, "[" + title + "]") : " " + title + " ");
        }

        var lines = new List<string> { string.Concat(parts) };
        if (Menu.IsOpen)
        {
            var current = Menu.Menus[Menu.MenuIndex];
            var items = current.Items
                .Select((item, i) => i == Menu.ItemIndex ? "[" + item.Title + "]" : " " + item.Title + " ");
            lines.Add(TextFit.Cut("  " + current.Title + ": " + string.Concat(items), context.Width));
        }

        return lines;
    }

    /// <summary>The title with its shortcut letter underlined when colour is on. Without colour the title is plain: the footer lists the keys.</summary>
    private static string Shortcut(string title, char? letter, TuiStyle style)
    {
        if (letter is not { } shortcut || !style.UseColor)
        {
            return title;
        }

        var at = title.IndexOf(shortcut.ToString(), StringComparison.OrdinalIgnoreCase);
        return at < 0 ? title : title[..at] + Ansi.Underline(style, title[at].ToString()) + title[(at + 1)..];
    }

    private List<string> BannerLines(ScreenContext context)
    {
        var lines = new List<string>();
        var banner = Alerts.Banner;
        foreach (var item in banner.Take(2))
        {
            lines.Add(context.Style.Error(context.Style.Bold(TextFit.Cut("!! SERIOUS: " + item.Text, context.Width))));
        }

        if (banner.Count > 2)
        {
            lines.Add(context.Style.Error(TextFit.Cut($"!! +{banner.Count - 2} more serious, press Tab to acknowledge", context.Width)));
        }

        if (Alerts.WinBanner.Count > 0)
        {
            var more = Alerts.WinBanner.Count > 1 ? $" (+{Alerts.WinBanner.Count - 1} more)" : string.Empty;
            var text = TextFit.Cut("FIXED: " + Alerts.WinBanner[0].Summary + more, context.Width);
            lines.Add(Ansi.Green(context.Style, context.Style.Bold(text)));
        }

        return lines;
    }

    private string FooterLine(int width)
    {
        var hint = Alerts.Banner.Count > 0 || Alerts.WinBanner.Count > 0
            ? "Arrows+Enter: menu  /: go to  Tab: acknowledge  Ctrl+C: leave"
            : "Arrows+Enter: menu  /: go to  Ctrl+C: leave";
        if (CurrentScreen == DefaultMenus.Logs)
        {
            hint = "[ ]: level  f: module  " + hint;
        }

        return _style.Dim(TextFit.Cut(hint, width));
    }
}
