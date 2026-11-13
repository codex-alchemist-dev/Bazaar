using System.Globalization;
using Bazaar.Contracts.Ryusei;

namespace Bazaar.Client.Core.Ryusei;

/// <summary>
/// One micro trigger as a task card shows it (doc 38, layer 1: micro links on a card). The shape mirrors the server's
/// task chip, so the client keeps no reference to the server project. Kind and state use the wire spelling, as
/// <see cref="RyuseiTriggerSummary"/> does.
/// </summary>
public sealed record RyuseiTaskChip(string TriggerId, string Kind, string State, DateTimeOffset? NextFire, string Purpose);

/// <summary>
/// Chip ordering and display text (doc 38, layer 1). Chips are read-only: a trigger that should move a card calls the
/// task service on the server, and the client never changes task state from a chip. Pure: no I/O and no clock.
/// </summary>
public static class RyuseiChipRules
{
    /// <summary>The longest purpose text a chip shows, counting the ellipsis.</summary>
    public const int MaxPurposeLength = 80;

    private const string _ellipsis = "…";

    /// <summary>
    /// Only active and paused triggers are shown, because they are the live states the owner can act on. Order: next
    /// fire (soonest first, no next fire last), then trigger id.
    /// </summary>
    public static IReadOnlyList<RyuseiTaskChip> Visible(IEnumerable<RyuseiTaskChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);

        return chips
            .Where(c => Is(c.State, "active") || Is(c.State, "paused"))
            .OrderBy(c => c.NextFire is null)
            .ThenBy(c => c.NextFire)
            .ThenBy(c => c.TriggerId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Joins the owner's trigger list (<c>ryusei.trigger.list</c>) to board cards by task id (doc 38, layer 1). A trigger with
    /// no task is on no card and is left out. Each card's chips are then filtered and ordered by <see cref="Visible"/>.
    /// Pure: no I/O.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<RyuseiTaskChip>> ByTask(IEnumerable<RyuseiTriggerChip> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        return triggers
            .Where(t => t.TaskId is not null)
            .GroupBy(t => t.TaskId!.Value)
            .ToDictionary(
                group => group.Key,
                group => Visible(group.Select(t => new RyuseiTaskChip(
                    t.TriggerId.ToString("D"), t.Kind, t.State, t.NextFireAt, t.Purpose))));
    }

    /// <summary>The first line of a chip: kind, state and the next fire time when there is one.</summary>
    public static string Status(RyuseiTaskChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);

        var parts = new List<string> { KindLabel(chip.Kind), StateLabel(chip.State) };
        if (chip.NextFire is { } fire)
        {
            // The offset is the one the server sent, so the text matches the server's record.
            parts.Add("Next " + fire.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The purpose text, cut to <see cref="MaxPurposeLength"/> with an ellipsis.</summary>
    public static string Purpose(RyuseiTaskChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);

        var purpose = chip.Purpose.Trim();
        if (purpose.Length <= MaxPurposeLength)
        {
            return purpose;
        }

        return string.Concat(purpose.AsSpan(0, MaxPurposeLength - _ellipsis.Length), _ellipsis);
    }

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string KindLabel(string kind) => kind.ToLowerInvariant() switch
    {
        "timer" => "Timer",
        "interval" => "Interval",
        "watcher" => "Watcher",
        "event" => "Event",
        _ => "Trigger",
    };

    private static string StateLabel(string state) => state.ToLowerInvariant() switch
    {
        "active" => "Active",
        "paused" => "Paused",
        _ => "Unknown state",
    };
}
