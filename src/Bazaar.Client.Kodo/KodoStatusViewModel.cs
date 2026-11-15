namespace Bazaar.Client.Kodo;

/// <summary>
/// The health state Kodo computes on the server (doc 46, section 8.1). Wire spelling is lower case. The client renders
/// the state and never derives it. Colours are owned by the theme, which maps each state to a colour token.
/// </summary>
public enum KodoHealthState
{
    Healthy,
    Degraded,
    Failing,
    Recovering,
    Frozen,
}

/// <summary>Controls the Monitor pane can offer. The server decides which exist for the caller and whether each is enabled.</summary>
public enum KodoControl
{
    Restart,
    Isolate,
    RollBack,
    IncidentExit,
}

/// <summary>
/// One control as the server sent it. <see cref="Enabled"/> and <see cref="DisabledReason"/> are copied from the server,
/// never derived here (doc 46, section 14; the server owns the freeze and authority rules).
/// </summary>
public sealed record KodoControlState(KodoControl Control, bool Enabled, string? DisabledReason);

/// <summary>
/// One status snapshot as the server sent it. <see cref="UnderlyingState"/> is the health state computed while incident
/// mode is active, so the pane can show "frozen, underlying failing". It is null when the state is not frozen.
/// <see cref="Controls"/> lists only the controls this caller may see.
/// </summary>
public sealed record KodoStatusSnapshot(
    KodoHealthState State,
    KodoHealthState? UnderlyingState,
    int Score,
    DateTimeOffset AsOf,
    IReadOnlyList<KodoControlState> Controls);

/// <summary>The display model of the Monitor status header (doc 46, sections 8, 12 and 14).</summary>
public sealed record KodoStatusRow(
    KodoHealthState State,
    string StateWord,
    string Headline,
    string ScoreText,
    string FreshnessText,
    bool IsStale,
    IReadOnlyList<KodoControlRow> Controls);

/// <summary>One control row for the header, with its label and the server's enabled flag and reason.</summary>
public sealed record KodoControlRow(KodoControl Control, string Label, bool Enabled, string? DisabledReason);

/// <summary>
/// Turns a server snapshot into the text and control rows of the Monitor status header. It orders and formats only: it
/// does not score, decide states, or decide what a control may do. Colour is not chosen here; the theme maps
/// <see cref="KodoStatusRow.State"/> to a colour token.
/// </summary>
public static class KodoStatusViewModel
{
    /// <summary>A snapshot older than this is shown as offline and stale (doc 46, section 14).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Builds the header row for one snapshot at the given time.</summary>
    public static KodoStatusRow Build(KodoStatusSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var headline = snapshot.State == KodoHealthState.Frozen && snapshot.UnderlyingState is { } underlying
            ? $"Frozen, underlying {Word(underlying).ToLowerInvariant()}"
            : Word(snapshot.State);

        var age = now - snapshot.AsOf;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var stale = age > StaleAfter;
        var freshness = stale
            ? $"Offline: last seen {FormatAge(age)} ago"
            : $"Updated {FormatAge(age)} ago";

        return new KodoStatusRow(
            State: snapshot.State,
            StateWord: Word(snapshot.State),
            Headline: headline,
            ScoreText: $"Score {Math.Clamp(snapshot.Score, 0, 100)} of 100",
            FreshnessText: freshness,
            IsStale: stale,
            Controls: snapshot.Controls.Select(c => new KodoControlRow(c.Control, Label(c.Control), c.Enabled, c.DisabledReason)).ToList());
    }

    /// <summary>Plain-English state word, always shown next to the theme colour.</summary>
    public static string Word(KodoHealthState state) => state switch
    {
        KodoHealthState.Healthy => "Healthy",
        KodoHealthState.Degraded => "Degraded",
        KodoHealthState.Failing => "Failing",
        KodoHealthState.Recovering => "Recovering",
        KodoHealthState.Frozen => "Frozen",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string Label(KodoControl control) => control switch
    {
        KodoControl.Restart => "Restart",
        KodoControl.Isolate => "Isolate",
        KodoControl.RollBack => "Roll back",
        KodoControl.IncidentExit => "Exit incident mode",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, null),
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
        {
            return "under a minute";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes} min";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours} h";
        }

        return $"{(int)age.TotalDays} d";
    }
}
