using System.Globalization;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard;

/// <summary>The log screen's filter (doc 58 section 3: filter by level and module). Changed by the shell's keys, applied by the next load.</summary>
public sealed class LogsFilterState
{
    private static readonly OpsLogLevel[] _levels = Enum.GetValues<OpsLogLevel>();

    public OpsLogLevel MinLevel { get; private set; } = OpsLogLevel.Information;

    public string? Module { get; set; }

    public void Raise() => MinLevel = _levels[Math.Min(_levels.Length - 1, (int)MinLevel + 1)];

    public void Lower() => MinLevel = _levels[Math.Max(0, (int)MinLevel - 1)];

    public OpsLogFilter ToFilter(int limit) =>
        new(MinLevel, string.IsNullOrWhiteSpace(Module) ? null : Module.Trim(), limit);
}

/// <summary>
/// The Ops screens other than the overview, as lists of text lines (doc 58 section 3): Kodo, Logs, Events and the Admin submenu
/// (Modules, Users, Approvals, Audit, Rebuilds). Every screen is read-only here; a missing or failing port reads "unavailable".
/// Server text (names, log messages, summaries) is cleaned before it is drawn.
/// </summary>
public static class OpsScreens
{
    public const string Unavailable = "unavailable (the server does not offer this section right now)";

    public static async Task<IReadOnlyList<string>> LoadAsync(
        string screenId,
        OpsPorts ports,
        ScreenContext context,
        LogsFilterState logsFilter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);
        return screenId switch
        {
            DefaultMenus.Kodo => await KodoAsync(ports, context, ct).ConfigureAwait(false),
            DefaultMenus.Logs => await LogsAsync(ports, context, logsFilter, ct).ConfigureAwait(false),
            DefaultMenus.Events => await EventsAsync(ports, context, ct).ConfigureAwait(false),
            DefaultMenus.Modules => await ModulesAsync(ports, context, ct).ConfigureAwait(false),
            DefaultMenus.Users => await UsersAsync(ports, context, ct).ConfigureAwait(false),
            DefaultMenus.Approvals => await ApprovalsAsync(ports, context, ct).ConfigureAwait(false),
            DefaultMenus.Audit => await AuditAsync(ports, context, ct).ConfigureAwait(false),
            DefaultMenus.Rebuilds => await RebuildsAsync(ports, context, ct).ConfigureAwait(false),
            _ => ["Unknown screen."],
        };
    }

    public static async Task<IReadOnlyList<string>> KodoAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var view = await SoftPort.TryAsync(ports.Kodo, (source, token) => source.GetAsync(token), ct).ConfigureAwait(false);
        if (view is null)
        {
            return Page("Kodo health", [], [Unavailable], context, tail: false);
        }

        var header = new List<string> { view.Score is { } score ? $"Score {score}" : "Score not yet known" };
        var items = new List<string>();
        foreach (var check in view.Checks.OrderByDescending(c => KodoSeverity.FromState(c.State)))
        {
            var severity = KodoSeverity.FromState(check.State);
            var detail = string.IsNullOrWhiteSpace(check.Detail) ? string.Empty : ", " + check.Detail;
            items.Add(HealthOverviewRenderer.AttentionText(
                new AttentionLine(severity, "check:" + check.Name, $"{check.Name} {check.State}{detail}"), context.Style, context.Width));
        }

        if (view.Incidents.Count > 0)
        {
            items.Add(string.Empty);
            items.Add(context.Style.Bold("Incidents"));
            foreach (var incident in view.Incidents)
            {
                var severity = KodoSeverity.FromIncident(incident.Severity);
                var link = string.IsNullOrWhiteSpace(incident.RunbookLink) ? string.Empty : " | runbook " + incident.RunbookLink;
                items.Add(HealthOverviewRenderer.AttentionText(
                    new AttentionLine(severity, "incident:" + incident.Id, $"{incident.Title} ({Time(incident.OpenedAt, context)}){link}"),
                    context.Style,
                    context.Width));
            }
        }

        return Page("Kodo health", header, items.Count == 0 ? ["No checks reported."] : items, context, tail: false);
    }

    public static async Task<IReadOnlyList<string>> LogsAsync(OpsPorts ports, ScreenContext context, LogsFilterState filter, CancellationToken ct)
    {
        var title = $"Logs (level {filter.MinLevel} and up, module {(string.IsNullOrWhiteSpace(filter.Module) ? "all" : TextFit.Clean(filter.Module))})";
        var lines = await SoftPort.TryAsync(
            ports.Logs, (source, token) => source.TailAsync(filter.ToFilter(Math.Max(1, context.Rows)), token), ct).ConfigureAwait(false);
        if (lines is null)
        {
            return Page(title, [], [Unavailable], context, tail: true);
        }

        var items = lines.Select(line =>
        {
            var text = $"{Time(line.At, context, seconds: true)} {line.Level,-11} {TextFit.Clean(line.Module)} {TextFit.Clean(line.Message)}";
            var cut = TextFit.Cut(text, context.Width);
            return line.Level switch
            {
                OpsLogLevel.Error or OpsLogLevel.Critical => context.Style.Error(cut),
                OpsLogLevel.Warning => context.Style.Warning(cut),
                _ => cut,
            };
        }).ToList();
        return Page(title, [], items.Count == 0 ? ["No log lines match."] : items, context, tail: true);
    }

    public static async Task<IReadOnlyList<string>> EventsAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var events = await ReadEventsAsync(ports, context.Rows, ct).ConfigureAwait(false);
        if (events is null)
        {
            return Page("Events", [], [Unavailable], context, tail: false);
        }

        var items = events.Select(e => HealthOverviewRenderer.EventText(e, context)).ToList();
        return Page("Events", [], items.Count == 0 ? ["No recent events."] : items, context, tail: false);
    }

    /// <summary>Events from the event source plus crashes of the TUI itself, newest first. Null only when neither port answered.</summary>
    public static async Task<IReadOnlyList<OpsEvent>?> ReadEventsAsync(OpsPorts ports, int limit, CancellationToken ct)
    {
        var events = await SoftPort.TryAsync(ports.Events, (source, token) => source.ReadRecentAsync(limit, token), ct).ConfigureAwait(false);
        var crashes = await SoftPort.TryAsync(ports.Crashes, (journal, token) => journal.ReadRecentAsync(limit, token), ct).ConfigureAwait(false);
        if (events is null && crashes is null)
        {
            return null;
        }

        return [.. (events ?? []).Concat((crashes ?? []).Select(HealthOverviewBuilder.CrashEvent)).OrderByDescending(e => e.At).Take(limit)];
    }

    public static async Task<IReadOnlyList<string>> ModulesAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var catalogue = await SoftPort.TryAsync(ports.Modules, (source, token) => source.GetCatalogueAsync(token), ct).ConfigureAwait(false);
        if (catalogue is null)
        {
            return Page("Modules", [], [Unavailable], context, tail: false);
        }

        var items = catalogue.Modules.Select(m =>
            TextFit.Cut($"[{(m.Selected ? "x" : " ")}] {m.Name} {m.Version} ({m.Id})", context.Width)).ToList();
        return Page($"Modules (selection revision {catalogue.Revision})", [], items.Count == 0 ? ["No modules listed."] : items, context, tail: false);
    }

    public static async Task<IReadOnlyList<string>> UsersAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var users = await SoftPort.TryAsync(ports.Users, (source, token) => source.ListAsync(token), ct).ConfigureAwait(false);
        var devices = await SoftPort.TryAsync(ports.Devices, (source, token) => source.ListAsync(token), ct).ConfigureAwait(false);
        if (users is null && devices is null)
        {
            return Page("Users and devices", [], [Unavailable], context, tail: false);
        }

        var items = new List<string>();
        foreach (var user in users ?? [])
        {
            var kind = user.IsAgent ? "agent" : "person";
            var state = user.Revoked ? "REVOKED" : "active";
            items.Add(TextFit.Cut($"{user.DisplayName} | {user.Role} | {kind} | {state}", context.Width));
        }

        if (devices is { Count: > 0 })
        {
            items.Add(string.Empty);
            items.Add(context.Style.Bold("Devices"));
            foreach (var device in devices)
            {
                var state = device.Revoked ? "REVOKED" : "active";
                items.Add(TextFit.Cut($"{device.Label} | last seen {Time(device.LastSeen, context)} | {state}", context.Width));
            }
        }

        return Page("Users and devices", users is null ? ["Users: unavailable"] : [], items.Count == 0 ? ["Nobody listed."] : items, context, tail: false);
    }

    public static async Task<IReadOnlyList<string>> ApprovalsAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var pending = await SoftPort.TryAsync(ports.Approvals, (source, token) => source.ListPendingAsync(token), ct).ConfigureAwait(false);
        if (pending is null)
        {
            return Page("Approvals", [], [Unavailable], context, tail: false);
        }

        var items = pending.Select(a => TextFit.Cut($"{Time(a.RequestedAt, context)} {a.Action} (asked by {a.Requester})", context.Width)).ToList();
        return Page("Approvals waiting", [], items.Count == 0 ? ["Nothing is waiting."] : items, context, tail: false);
    }

    public static async Task<IReadOnlyList<string>> AuditAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var page = await SoftPort.TryAsync(ports.Audit, (source, token) => source.ReadAsync(null, Math.Max(1, context.Rows), token), ct).ConfigureAwait(false);
        if (page is null)
        {
            return Page("Audit", [], [Unavailable], context, tail: false);
        }

        var items = page.Rows.Select(r => TextFit.Cut($"{Time(r.Ts, context)} {r.Actor} {r.Kind} {r.Summary}", context.Width)).ToList();
        return Page("Audit (newest first)", [], items.Count == 0 ? ["The audit log is empty."] : items, context, tail: false);
    }

    public static async Task<IReadOnlyList<string>> RebuildsAsync(OpsPorts ports, ScreenContext context, CancellationToken ct)
    {
        var builds = await SoftPort.TryAsync(ports.Modules, (source, token) => source.GetBuildsAsync(token), ct).ConfigureAwait(false);
        if (builds is null)
        {
            return Page("Rebuilds", [], [Unavailable], context, tail: false);
        }

        var items = builds.OrderByDescending(b => b.StartedAt).Select(b =>
        {
            var report = string.IsNullOrWhiteSpace(b.ReportSummary) ? string.Empty : " | " + b.ReportSummary;
            return TextFit.Cut($"{Time(b.StartedAt, context)} {b.BuildId} {b.State} (attempt {b.Attempt}){report}", context.Width);
        }).ToList();
        return Page("Rebuilds and build reports", [], items.Count == 0 ? ["No builds yet."] : items, context, tail: false);
    }

    private static string Time(DateTimeOffset at, ScreenContext context, bool seconds = false) =>
        TimeZoneInfo.ConvertTime(at, context.Zone).ToString(seconds ? "HH:mm:ss" : "MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// A title, optional header lines and a list that fills the rest of the rows. A list that does not fit keeps its head (newest
    /// first lists) or its tail (a log tail) and says how many lines it left out.
    /// </summary>
    internal static IReadOnlyList<string> Page(string title, IReadOnlyList<string> header, IReadOnlyList<string> items, ScreenContext context, bool tail)
    {
        var lines = new List<string> { context.Style.Bold(TextFit.Cut(title, context.Width)) };
        lines.AddRange(header.Select(h => TextFit.Cut(h, context.Width)));
        var room = Math.Max(0, context.Rows - lines.Count);
        if (items.Count <= room)
        {
            lines.AddRange(items);
        }
        else if (room > 0)
        {
            var shown = room - 1;
            var left = items.Count - shown;
            if (tail)
            {
                lines.Add($"({left} earlier lines not shown)");
                lines.AddRange(items.Skip(items.Count - shown));
            }
            else
            {
                lines.AddRange(items.Take(shown));
                lines.Add($"(+{left} more)");
            }
        }

        return lines;
    }
}
