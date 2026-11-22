namespace Bazaar.Client.Tui.Dashboard;

/// <summary>One serious failure to show in the banner. <see cref="Key"/> is stable for the same failure across polls.</summary>
public sealed record AlertItem(string Key, string Text);

/// <summary>
/// The alert rules of doc 58 section 3a, without a screen. A serious failure raises a banner and asks for one bell; the banner stays
/// until acknowledged, even if the failure clears meanwhile. A failure that stays serious after acknowledgement does not come back
/// and does not ring again; one that clears and recurs is new. Minor failures never come here: they are only an amber line on the
/// overview. Bigger wins (<see cref="OpsEvent.Major"/>) show a green banner until dismissed.
/// </summary>
public sealed class AlertCenter
{
    private readonly List<AlertItem> _banner = [];
    private readonly HashSet<string> _known = [];
    private readonly HashSet<string> _dismissedWins = [];
    private readonly List<OpsEvent> _winBanner = [];

    /// <summary>The bell can be turned off in settings (doc 58 section 3a).</summary>
    public bool BellEnabled { get; set; } = true;

    /// <summary>The unacknowledged serious failures, oldest first.</summary>
    public IReadOnlyList<AlertItem> Banner => _banner;

    /// <summary>The undismissed major wins, newest first.</summary>
    public IReadOnlyList<OpsEvent> WinBanner => _winBanner;

    /// <summary>
    /// Feeds the serious failures of this poll. Returns true when the terminal bell should ring, which is at most once per call and
    /// only for a failure not seen before (and only when the bell is on).
    /// </summary>
    public bool Observe(IEnumerable<AlertItem> serious)
    {
        ArgumentNullException.ThrowIfNull(serious);
        var current = serious.DistinctBy(item => item.Key).ToList();
        var currentKeys = current.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        _known.RemoveWhere(key => !currentKeys.Contains(key));

        var fresh = false;
        foreach (var item in current)
        {
            if (!_known.Add(item.Key))
            {
                continue;
            }

            fresh = true;
            var existing = _banner.FindIndex(candidate => candidate.Key == item.Key);
            if (existing >= 0)
            {
                _banner[existing] = item;
            }
            else
            {
                _banner.Add(item);
            }
        }

        return fresh && BellEnabled;
    }

    /// <summary>Acknowledges every failure in the banner. They stay quiet while they stay serious.</summary>
    public void Acknowledge() => _banner.Clear();

    /// <summary>Feeds the recent events; each major win not yet dismissed is shown in the green banner.</summary>
    public void ObserveWins(IEnumerable<OpsEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _winBanner.Clear();
        _winBanner.AddRange(events.Where(e => e.IsWin && e.Major && !_dismissedWins.Contains(e.Id)).OrderByDescending(e => e.At));
    }

    /// <summary>Dismisses the green banner for every win it shows.</summary>
    public void DismissWins()
    {
        foreach (var win in _winBanner)
        {
            _dismissedWins.Add(win.Id);
        }

        _winBanner.Clear();
    }
}
