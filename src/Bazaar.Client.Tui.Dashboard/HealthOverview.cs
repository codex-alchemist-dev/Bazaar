using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard;

/// <summary>One thing that needs attention on the overview: a Kodo check or incident that is not fine.</summary>
public sealed record AttentionLine(OpsSeverity Severity, string Key, string Text);

/// <summary>The health overview as data (doc 58 section 3, the default screen). Built from the ports, drawn by <see cref="HealthOverviewRenderer"/>.</summary>
public sealed record HealthOverview(
    string? ServerLine,
    IReadOnlyList<string> ProviderLines,
    string KodoLine,
    IReadOnlyList<AttentionLine> Attention,
    IReadOnlyList<OpsEvent> Events,
    string? DailySummary,
    int OkChecks,
    bool StatusUnavailable,
    bool KodoUnavailable)
{
    /// <summary>The serious items, as banner input for <see cref="AlertCenter"/>.</summary>
    public IEnumerable<AlertItem> SeriousItems =>
        Attention.Where(line => line.Severity == OpsSeverity.Serious).Select(line => new AlertItem(line.Key, line.Text));
}

public static class HealthOverviewBuilder
{
    /// <summary>The crash of the TUI itself becomes a line on the next overview (doc 58 section 3a).</summary>
    public static OpsEvent CrashEvent(OpsCrashNote note) =>
        new("tui-crash:" + note.At.UtcTicks, note.At, OpsEventKind.Fault, "Resident TUI crashed and restarted: " + TextFit.Clean(note.Reason));

    public static HealthOverview Build(
        AdminStatusView? status,
        AdminKodoView? kodo,
        IEnumerable<OpsEvent> events,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(zone);

        var attention = new List<AttentionLine>();
        var ok = 0;
        string kodoLine;
        if (kodo is null)
        {
            kodoLine = "Kodo: unavailable";
        }
        else
        {
            foreach (var check in kodo.Checks)
            {
                var severity = KodoSeverity.FromState(check.State);
                if (severity == OpsSeverity.Ok)
                {
                    ok++;
                    continue;
                }

                var detail = string.IsNullOrWhiteSpace(check.Detail) ? string.Empty : ": " + check.Detail;
                attention.Add(new AttentionLine(severity, "check:" + check.Name, $"{check.Name} is {TextFit.Clean(check.State)}{detail}"));
            }

            foreach (var incident in kodo.Incidents)
            {
                var severity = KodoSeverity.FromIncident(incident.Severity);
                if (severity != OpsSeverity.Ok)
                {
                    attention.Add(new AttentionLine(severity, "incident:" + incident.Id, "Incident: " + TextFit.Clean(incident.Title)));
                }
            }

            attention = [.. attention.OrderByDescending(line => line.Severity)];
            var serious = attention.Count(line => line.Severity == OpsSeverity.Serious);
            var minor = attention.Count - serious;
            var score = kodo.Score is { } value ? $"score {value}, " : string.Empty;
            kodoLine = $"Kodo: {score}{ok} ok, {minor} minor, {serious} serious";
        }

        string? serverLine = null;
        IReadOnlyList<string> providers = [];
        if (status is not null)
        {
            var codename = string.IsNullOrWhiteSpace(status.Codename) ? string.Empty : $" \"{status.Codename}\"";
            serverLine = $"Server: {TextFit.Clean(status.DisplayName)} {TextFit.Clean(status.Version)}{codename}, up {TextFit.Duration(status.UptimeSeconds)}";
            providers = status.ProviderLines;
        }

        var recent = events.OrderByDescending(e => e.At).ToList();
        return new HealthOverview(serverLine, providers, kodoLine, attention, recent, DailySummary(recent, now, zone), ok, status is null, kodo is null);
    }

    /// <summary>"N things fixed themselves today" (owner card 2026-11-18). Null when nothing did, so a quiet day shows no line.</summary>
    public static string? DailySummary(IEnumerable<OpsEvent> events, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var count = events.Count(e => e.FixedItself && TimeZoneInfo.ConvertTime(e.At, zone).Date == today);
        return count switch
        {
            0 => null,
            1 => "1 thing fixed itself today",
            _ => $"{count} things fixed themselves today",
        };
    }
}

public static class HealthOverviewRenderer
{
    private const int _providerLines = 2;

    /// <summary>
    /// Draws the overview into at most <see cref="ScreenContext.Rows"/> lines of at most <see cref="ScreenContext.Width"/> visible
    /// characters, so it fits an 80x24 terminal (doc 58 section 3). Lists give way to a "+N more" line, never to an overflow.
    /// </summary>
    public static IReadOnlyList<string> Render(HealthOverview overview, ScreenContext context)
    {
        var style = context.Style;
        var width = context.Width;
        var lines = new List<string>
        {
            TextFit.Cut(overview.ServerLine ?? "Server: unavailable", width),
        };
        lines.AddRange(overview.ProviderLines.Take(_providerLines).Select(line => "  " + TextFit.Cut(line, width - 2)));
        lines.Add(TextFit.Cut(overview.KodoLine, width));
        if (overview.DailySummary is { } summary)
        {
            lines.Add(Ansi.Green(style, TextFit.Cut(summary, width)));
        }

        // Two section headers are fixed; the rest is shared, attention first.
        var remaining = Math.Max(0, context.Rows - lines.Count - 2);
        var attentionBudget = overview.Attention.Count == 0 ? 1 : Math.Min(overview.Attention.Count, Math.Max(1, remaining / 2));
        var eventBudget = Math.Max(0, remaining - attentionBudget);
        if (overview.Events.Count > 0 && eventBudget == 0 && attentionBudget > 1)
        {
            attentionBudget--;
            eventBudget = 1;
        }

        lines.Add(Heading(style, "Needs attention", width));
        if (overview.Attention.Count == 0)
        {
            lines.Add(overview.KodoUnavailable ? "Kodo unavailable, so nothing can be said." : "Nothing needs attention.");
        }
        else
        {
            var shownAttention = Limit(overview.Attention, attentionBudget, out var more, count => $"+{count} more, see Kodo");
            foreach (var line in shownAttention)
            {
                lines.Add(AttentionText(line, style, width));
            }

            if (more is not null)
            {
                lines.Add(more);
            }
        }

        lines.Add(Heading(style, "Recent events", width));
        if (overview.Events.Count == 0)
        {
            lines.Add("No recent events.");
        }
        else
        {
            var shownEvents = Limit(overview.Events, eventBudget, out var more, count => $"+{count} more, see Events");
            foreach (var evt in shownEvents)
            {
                lines.Add(EventText(evt, context));
            }

            if (more is not null)
            {
                lines.Add(more);
            }
        }

        return lines.Take(context.Rows).ToList();
    }

    /// <summary>One event line: time, a word for what it is, the summary. Wins are green and also say so in words.</summary>
    public static string EventText(OpsEvent evt, ScreenContext context)
    {
        var time = TimeZoneInfo.ConvertTime(evt.At, context.Zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var word = EventWord(evt.Kind);
        var prefix = $"{time} [{word}] ";
        var text = prefix + TextFit.Cut(evt.Summary, Math.Max(0, context.Width - prefix.Length));
        if (evt.IsWin)
        {
            return Ansi.Green(context.Style, text);
        }

        return evt.Kind == OpsEventKind.Fault ? context.Style.Warning(text) : text;
    }

    public static string EventWord(OpsEventKind kind) => kind switch
    {
        OpsEventKind.Fault => "fault",
        OpsEventKind.SelfHeal => "self-healed",
        OpsEventKind.Recovery => "recovered",
        OpsEventKind.RestartWorked => "restarted",
        OpsEventKind.CrashSurvived => "survived crash",
        OpsEventKind.SlotRollback => "rolled back",
        OpsEventKind.BackupVerified => "backup ok",
        _ => "info",
    };

    internal static string AttentionText(AttentionLine line, TuiStyle style, int width)
    {
        var tag = "[" + KodoSeverity.Label(line.Severity) + "] ";
        var text = tag + TextFit.Cut(line.Text, Math.Max(0, width - tag.Length));
        return line.Severity switch
        {
            OpsSeverity.Serious => style.Error(text),
            OpsSeverity.Minor => style.Warning(text),
            _ => text,
        };
    }

    private static string Heading(TuiStyle style, string title, int width) =>
        style.Bold(TextFit.Cut("-- " + title + " " + new string('-', Math.Max(0, width - title.Length - 4)), width));

    /// <summary>Takes up to <paramref name="budget"/> lines, using the last one for "+N more" when the list is longer.</summary>
    private static IEnumerable<T> Limit<T>(IReadOnlyList<T> items, int budget, out string? more, Func<int, string> moreText)
    {
        if (items.Count <= budget)
        {
            more = null;
            return items;
        }

        var shown = Math.Max(0, budget - 1);
        more = budget >= 1 ? moreText(items.Count - shown) : null;
        return items.Take(shown);
    }
}
