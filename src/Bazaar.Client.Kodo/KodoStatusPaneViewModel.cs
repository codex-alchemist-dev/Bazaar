using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo;

/// <summary>
/// One score cap the server applied (doc 46 section 12.2). Caps are a safety floor, so the pane names each one beside the score.
/// </summary>
/// <param name="Reason">Plain-English reason, for example "A critical probe is failing".</param>
/// <param name="Limit">The cap, from 0 to 100.</param>
public sealed record KodoScoreCap(string Reason, int Limit);

/// <summary>
/// Facts the Status view shows that the stream snapshot does not carry yet (open alerts, caps). They come from the read API
/// <c>GET /status</c> (doc 46 section 15.1) once that DTO exists in Contracts; until then a head passes null and the view says
/// "not reported". PROPOSED seam, see doc 46 section 14.6.
/// </summary>
public sealed record KodoStatusExtras(int? OpenAlerts, IReadOnlyList<KodoScoreCap> ScoreCaps);

/// <summary>One control the viewer may see, with the server's enabled flag and reason.</summary>
public sealed record KodoControlPanelRow(KodoControl Control, string Label, bool Enabled, string? DisabledReason, string AccessibleName);

/// <summary>The theme token names for each state (doc 46 section 8.1). The theme owns the colour, the word always shows too.</summary>
public static class KodoStateTokens
{
    /// <summary>Token used when the theme defines no Kodo state colours.</summary>
    public const string Fallback = "Bazaar.brush.color.text.primary";

    /// <summary>The proposed theme brush key for a state.</summary>
    public static string For(KodoDisplayState state) => "Bazaar.brush.kodo.state." + state switch
    {
        KodoDisplayState.Healthy => "healthy",
        KodoDisplayState.Degraded => "degraded",
        KodoDisplayState.Failing => "failing",
        KodoDisplayState.Recovering => "recovering",
        KodoDisplayState.Frozen => "frozen",
        KodoDisplayState.Unobserved => "unobserved",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

/// <summary>The display model of the Status view (doc 46 section 14.2, view 1). Plain data, no controls.</summary>
public sealed record KodoStatusPanel(
    bool Permitted,
    KodoDisplayState State,
    string StateWord,
    string Headline,
    string ColourToken,
    string ScoreText,
    IReadOnlyList<string> ScoreCapTexts,
    string OpenAlertsText,
    bool OpenIncident,
    string IncidentText,
    bool IsOffline,
    string OfflineText,
    string ComponentsText,
    IReadOnlyList<KodoControlPanelRow> Controls,
    string AccessibleSummary);

/// <summary>
/// Pure view model of the Status view. It formats what the reducer holds and the server computed; it never derives a health
/// state, score or control right. <c>Unobserved</c> (doc 46 section 8.3) replaces every other word and the score is
/// hidden behind "last known", so a dead stream never reads as healthy.
/// </summary>
public sealed class KodoStatusPaneViewModel(KodoStreamReducer reducer, IKodoPermissions permissions)
{
    private readonly KodoStreamReducer _reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
    private readonly IKodoPermissions _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

    /// <summary>Extra read API facts, or null while the head has none.</summary>
    public KodoStatusExtras? Extras { get; set; }

    /// <summary>Raised when a viewer asks for an enabled control. The head routes it to the Responder; nothing runs here.</summary>
    public event EventHandler<KodoControl>? ControlRequested;

    /// <summary>The reducer this view model reads, so a view can refresh when it changes.</summary>
    public KodoStreamReducer Reducer => _reducer;

    /// <summary>Asks for a control. Returns false (and raises nothing) when it is not shown, disabled by the server, or not permitted.</summary>
    public bool RequestControl(KodoControl control)
    {
        var row = Build().Controls.FirstOrDefault(c => c.Control == control);
        if (row is not { Enabled: true })
        {
            return false;
        }

        ControlRequested?.Invoke(this, control);
        return true;
    }

    /// <summary>Builds the panel for the reducer's state right now.</summary>
    public KodoStatusPanel Build()
    {
        var display = _reducer.Display();
        var offline = display == KodoDisplayState.Unobserved;
        var status = _reducer.Status;

        if (!_permissions.Can(KodoPermissions.ViewStatus))
        {
            return Denied(display);
        }

        var word = StateWord(display);
        var headline = word;
        var scoreText = "Score not available";
        var incident = false;
        var incidentText = "No open incident";
        if (status is not null)
        {
            var score = Math.Clamp(status.Score, 0, 100);
            scoreText = offline ? $"Last known score {score} of 100" : $"Score {score} of 100";
            if (!offline
                && KodoUnobserved.TryDisplayOf(status.State, out var shown)
                && shown == KodoDisplayState.Frozen)
            {
                incident = true;
                incidentText = "Incident mode is open";
                if (KodoUnobserved.TryDisplayOf(status.UnderlyingState, out var under))
                {
                    headline = $"Frozen, underlying {StateWord(under).ToLowerInvariant()}";
                }
            }
        }

        var silent = _reducer.SilentFor();
        var offlineText = offline
            ? silent is { } age
                ? $"Offline: last event {KodoStatusViewModel.FormatAge(age)} ago"
                : "Offline: no event received yet"
            : string.Empty;
        if (offline && _reducer.NeedsSnapshot && silent is not null)
        {
            offlineText += ", waiting for a fresh snapshot";
        }

        var caps = Extras is { } extras && !offline
            ? extras.ScoreCaps.Select(c => $"Capped at {c.Limit}: {c.Reason}").ToList()
            : [];
        var alertsText = Extras?.OpenAlerts is { } alerts && !offline
            ? alerts == 1 ? "1 open alert" : $"{alerts} open alerts"
            : "Open alerts not reported";

        var components = _reducer.Components;
        var notGreen = components.Count(c => c.State != "green");
        var componentsText = components.Count == 0
            ? "No component data"
            : notGreen == 0 ? $"All {components.Count} components green" : $"{notGreen} of {components.Count} components not green";

        var controls = new List<KodoControlPanelRow>();
        if (!offline && status is not null && KodoStatusMapper.TryMap(status, out var mapped, out _))
        {
            foreach (var control in mapped!.Controls.Where(c => _permissions.Can(KodoPermissions.For(c.Control))))
            {
                var label = KodoStatusViewModel.Label(control.Control);
                var name = control.Enabled ? label : $"{label}, unavailable: {control.DisabledReason ?? "not allowed now"}";
                controls.Add(new KodoControlPanelRow(control.Control, label, control.Enabled, control.DisabledReason, name));
            }
        }

        var summary = $"Kodo status: {headline}. {scoreText}. {(offline ? offlineText : alertsText)}. {incidentText}.";
        return new KodoStatusPanel(
            true, display, word, headline, KodoStateTokens.For(display), scoreText, caps, alertsText,
            incident, incidentText, offline, offlineText, componentsText, controls, summary);
    }

    private static KodoStatusPanel Denied(KodoDisplayState display) => new(
        false, display, "Not permitted", "Not permitted", KodoStateTokens.Fallback, string.Empty, [], string.Empty,
        false, string.Empty, false, string.Empty, string.Empty, [], "Kodo status is not available for your role.");

    private static string StateWord(KodoDisplayState state) => state switch
    {
        KodoDisplayState.Healthy => KodoStatusViewModel.Word(KodoHealthState.Healthy),
        KodoDisplayState.Degraded => KodoStatusViewModel.Word(KodoHealthState.Degraded),
        KodoDisplayState.Failing => KodoStatusViewModel.Word(KodoHealthState.Failing),
        KodoDisplayState.Recovering => KodoStatusViewModel.Word(KodoHealthState.Recovering),
        KodoDisplayState.Frozen => KodoStatusViewModel.Word(KodoHealthState.Frozen),
        KodoDisplayState.Unobserved => "Unobserved",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
