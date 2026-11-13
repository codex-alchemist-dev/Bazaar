namespace Bazaar.Client.Core.Kodo;

/// <summary>
/// The health state Kodo computes on the server (doc 46, section 8.1). Wire spelling is lower case. The client renders
/// the state and never derives it.
/// </summary>
public enum KodoHealthState
{
    Healthy,
    Degraded,
    Failing,
    Recovering,
    Frozen,
}

/// <summary>Colour token for the status word. Colour is never the only signal (doc 46, section 12.1).</summary>
public enum KodoColourToken
{
    Green,
    Amber,
    Red,
    Incident,
}

/// <summary>
/// One status snapshot as the server sent it. <see cref="UnderlyingState"/> is the health state computed while incident
/// mode is active, so the pane can show "frozen, underlying failing". It is null when the state is not frozen.
/// </summary>
public sealed record KodoStatusSnapshot(
    KodoHealthState State,
    KodoHealthState? UnderlyingState,
    int Score,
    DateTimeOffset AsOf);

/// <summary>Controls the Monitor pane can offer. Each needs a role grant (doc 46, section 14).</summary>
public enum KodoControl
{
    Restart,
    Isolate,
    RollBack,
    IncidentExit,
}

/// <summary>One control row: whether it is shown, whether it can be pressed, and why it cannot.</summary>
public sealed record KodoControlRow(KodoControl Control, string Label, bool Enabled, string? DisabledReason);

/// <summary>The display model of the Monitor status header (doc 46, sections 8, 12 and 14).</summary>
public sealed record KodoStatusRow(
    string StateWord,
    KodoColourToken Colour,
    string Headline,
    string ScoreText,
    string FreshnessText,
    bool IsStale,
    IReadOnlyList<KodoControlRow> Controls);

/// <summary>
/// Turns a server snapshot into the text, colour and control rows of the Monitor status header. It orders and formats
/// only: it does not score, decide states or judge controls beyond the role grant and the incident freeze rule.
/// </summary>
public static class KodoStatusViewModel
{
    /// <summary>A snapshot older than this is shown as offline and stale (doc 46, section 14).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Role grant names used by the Monitor pane controls.</summary>
    public const string RestartGrant = "kodo.restart";
    public const string IsolateGrant = "kodo.isolate";
    public const string RollBackGrant = "kodo.rollback";
    public const string IncidentExitGrant = "kodo.incident.exit";

    /// <summary>Builds the header row for one snapshot, the current time and the caller's role grants.</summary>
    public static KodoStatusRow Build(KodoStatusSnapshot snapshot, DateTimeOffset now, IReadOnlySet<string> grants)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(grants);

        var frozen = snapshot.State == KodoHealthState.Frozen;
        var headline = frozen && snapshot.UnderlyingState is { } underlying
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
            StateWord: Word(snapshot.State),
            Colour: Colour(snapshot.State),
            Headline: headline,
            ScoreText: $"Score {Math.Clamp(snapshot.Score, 0, 100)} of 100",
            FreshnessText: freshness,
            IsStale: stale,
            Controls: Controls(frozen, grants));
    }

    /// <summary>Plain-English state word, always shown with its colour.</summary>
    public static string Word(KodoHealthState state) => state switch
    {
        KodoHealthState.Healthy => "Healthy",
        KodoHealthState.Degraded => "Degraded",
        KodoHealthState.Failing => "Failing",
        KodoHealthState.Recovering => "Recovering",
        KodoHealthState.Frozen => "Frozen",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>The colour token for a state. Frozen uses the incident colour.</summary>
    public static KodoColourToken Colour(KodoHealthState state) => state switch
    {
        KodoHealthState.Healthy => KodoColourToken.Green,
        KodoHealthState.Degraded or KodoHealthState.Recovering => KodoColourToken.Amber,
        KodoHealthState.Failing => KodoColourToken.Red,
        KodoHealthState.Frozen => KodoColourToken.Incident,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static List<KodoControlRow> Controls(bool frozen, IReadOnlySet<string> grants)
    {
        var rows = new List<KodoControlRow>(4);
        foreach (var (control, label, grant) in new[]
        {
            (KodoControl.Restart, "Restart", RestartGrant),
            (KodoControl.Isolate, "Isolate", IsolateGrant),
            (KodoControl.RollBack, "Roll back", RollBackGrant),
            (KodoControl.IncidentExit, "Exit incident mode", IncidentExitGrant),
        })
        {
            // Controls show only for a role that holds the grant. Incident exit is the only control allowed while frozen.
            if (!grants.Contains(grant))
            {
                continue;
            }

            var blocked = frozen && control != KodoControl.IncidentExit;
            rows.Add(new KodoControlRow(
                control,
                label,
                Enabled: !blocked,
                DisabledReason: blocked ? "Incident mode is active; risky actions are blocked" : null));
        }

        return rows;
    }

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
