using System.Globalization;

namespace Bazaar.Client.Core.Ryusei;

/// <summary>
/// One micro-schedule trigger as the server sent it (doc 38, layer 2). Kinds and states use the wire spelling:
/// kind <c>timer</c>, <c>interval</c>, <c>watcher</c> or <c>event</c>; state <c>active</c>, <c>paused</c>,
/// <c>fired</c>, <c>expired</c> or <c>cancelled</c>. <see cref="NextFire"/> is copied from the server, never derived here.
/// </summary>
public sealed record RyuseiTriggerSummary(
    string Id,
    string Kind,
    string State,
    DateTimeOffset? NextFire,
    IReadOnlyList<string> TargetNames,
    string Purpose);

/// <summary>
/// One display row of the micro schedule pane. All text is plain English, ready to bind. <see cref="State"/> is the wire
/// state, so the view can choose between Pause and Resume.
/// </summary>
public sealed record RyuseiMicroRow(
    string Id,
    string KindLabel,
    string StateLabel,
    string State,
    DateTimeOffset? NextFire,
    string NextFireText,
    string TargetsText,
    string Purpose);

/// <summary>
/// View model for the micro schedule pane (doc 38, layer 2). It only orders and formats the trigger list the server
/// sent. It does not compute fire times, cron-like rules or work-life windows; those belong to the server.
/// Order: active triggers with a next fire (soonest first), then paused triggers (soonest first, no next fire last),
/// then everything else. Ties break on the trigger id.
/// </summary>
public sealed class RyuseiMicroPaneViewModel
{
    /// <summary>Heading shown on the pane.</summary>
    public const string Title = "RyuseiFuda · Schedule";

    private const string _noTargets = "No targets";
    private const string _noNextFire = "No next fire";

    public RyuseiMicroPaneViewModel(IEnumerable<RyuseiTriggerSummary> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var all = triggers.ToList();

        var upcoming = all
            .Where(t => Is(t.State, "active") && t.NextFire is not null)
            .OrderBy(t => t.NextFire)
            .ThenBy(t => t.Id, StringComparer.Ordinal);

        var paused = all
            .Where(t => Is(t.State, "paused"))
            .OrderBy(t => t.NextFire is null)
            .ThenBy(t => t.NextFire)
            .ThenBy(t => t.Id, StringComparer.Ordinal);

        var rest = all
            .Where(t => !(Is(t.State, "active") && t.NextFire is not null) && !Is(t.State, "paused"))
            .OrderBy(t => t.Id, StringComparer.Ordinal);

        Rows = upcoming.Concat(paused).Concat(rest).Select(ToRow).ToArray();
    }

    /// <summary>Rows in display order. Empty when the server sent no triggers.</summary>
    public IReadOnlyList<RyuseiMicroRow> Rows { get; }

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static RyuseiMicroRow ToRow(RyuseiTriggerSummary trigger) =>
        new(
            trigger.Id,
            KindLabel(trigger.Kind),
            StateLabel(trigger.State),
            trigger.State,
            trigger.NextFire,
            NextFireText(trigger),
            TargetsText(trigger.TargetNames),
            trigger.Purpose);

    private static string KindLabel(string kind) => kind.ToLowerInvariant() switch
    {
        "timer" => "One-time timer",
        "interval" => "Repeating interval",
        "watcher" => "Condition watcher",
        "event" => "One-shot event",
        _ => "Unknown trigger type",
    };

    private static string StateLabel(string state) => state.ToLowerInvariant() switch
    {
        "active" => "Active",
        "paused" => "Paused",
        "fired" => "Fired",
        "expired" => "Expired",
        "cancelled" => "Cancelled",
        _ => "Unknown state",
    };

    /// <summary>Only active and paused triggers show a fire time. Finished ones show the plain label.</summary>
    private static string NextFireText(RyuseiTriggerSummary trigger)
    {
        if (trigger.NextFire is not { } fire || !(Is(trigger.State, "active") || Is(trigger.State, "paused")))
        {
            return _noNextFire;
        }

        // The offset is the one the server sent, so the text matches the server's record.
        return "Next " + fire.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>Blank names are dropped. An empty or all-blank list reads as "No targets".</summary>
    private static string TargetsText(IReadOnlyList<string> names)
    {
        var kept = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
        return kept.Length == 0 ? _noTargets : string.Join(", ", kept);
    }
}
