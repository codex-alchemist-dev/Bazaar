namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>One micro trigger as a task card shows it (doc 38, Layer 1: micro links on a card).</summary>
public sealed record RyuseiTaskChip(
    Guid TriggerId,
    RyuseiTriggerKind Kind,
    RyuseiTriggerState State,
    string Purpose,
    DateTimeOffset? NextFireAt);

/// <summary>
/// Chips are read-only. A trigger that should move a card calls kanban.transition through the task service (doc 38,
/// conflict 2); this model never changes task state.
/// </summary>
/// <remarks>
/// Only triggers with a task are chipped. Only <see cref="RyuseiTriggerState.Active"/> and
/// <see cref="RyuseiTriggerState.Paused"/> are shown, because they are the live states the owner can act on. Fired,
/// expired and cancelled triggers are terminal and are not shown. The model is pure: no I/O, no clock.
/// </remarks>
public static class RyuseiTaskChips
{
    /// <summary>The longest purpose text a chip carries, counting the ellipsis.</summary>
    public const int MaxPurposeLength = 80;

    private const string _ellipsis = "…";

    /// <summary>The chips for every task that has at least one live trigger, keyed by task id.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<RyuseiTaskChip>> ForBoard(IEnumerable<RyuseiTriggerRecord> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var chips = new Dictionary<Guid, IReadOnlyList<RyuseiTaskChip>>();
        var grouped = triggers
            .Where(IsChipped)
            .GroupBy(t => t.TaskId!.Value);

        foreach (var group in grouped)
        {
            chips[group.Key] = Order(group.Select(ToChip)).ToList();
        }

        return chips;
    }

    /// <summary>The chips for one task, ordered by next fire time (nulls last), then by trigger id.</summary>
    public static IReadOnlyList<RyuseiTaskChip> ForTask(IEnumerable<RyuseiTriggerRecord> triggers, Guid taskId)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var matching = triggers
            .Where(t => IsChipped(t) && t.TaskId == taskId)
            .Select(ToChip);

        return Order(matching).ToList();
    }

    private static bool IsChipped(RyuseiTriggerRecord trigger) =>
        trigger.TaskId is not null
        && trigger.State is RyuseiTriggerState.Active or RyuseiTriggerState.Paused;

    private static RyuseiTaskChip ToChip(RyuseiTriggerRecord trigger) => new(
        trigger.TriggerId,
        trigger.Kind,
        trigger.State,
        TruncatePurpose(trigger.Purpose),
        trigger.NextFireAt);

    private static IOrderedEnumerable<RyuseiTaskChip> Order(IEnumerable<RyuseiTaskChip> chips) =>
        chips
            .OrderBy(c => c.NextFireAt is null)
            .ThenBy(c => c.NextFireAt)
            .ThenBy(c => c.TriggerId);

    private static string TruncatePurpose(string purpose)
    {
        if (purpose.Length <= MaxPurposeLength)
        {
            return purpose;
        }

        return string.Concat(purpose.AsSpan(0, MaxPurposeLength - _ellipsis.Length), _ellipsis);
    }
}
