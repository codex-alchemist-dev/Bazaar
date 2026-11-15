namespace Bazaar.Server.Kanban;

/// <summary>
/// Settings for the deadline scanner (doc 12, WP11), bound from <c>Bazaar:Kanban:Deadline</c>.
/// <para>
/// The setting is named <c>kanban.deadline_mode</c> in the WP11 brief. Doc 12 names it <c>kanban.due_mode</c> (soft and hard),
/// and that name is still open with the owner. See the TODO on <see cref="DeadlineMode"/>.
/// </para>
/// </summary>
public sealed class DeadlineScanOptions
{
    public const string SectionName = "Bazaar:Kanban:Deadline";

    /// <summary>Maximum candidate tasks handled per run. Leftovers are picked up by the next run, in due-date order.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary><see cref="DeadlineMode.Notify"/> is the default: an event only, no state change.</summary>
    public DeadlineMode Mode { get; set; } = DeadlineMode.Notify;

    /// <summary>How often the scanner is enqueued as a recurring job. Whole seconds, at least one second.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>How a passed due date is handled.</summary>
public enum DeadlineMode
{
    /// <summary>Append <c>task.due_passed</c> and change nothing else.</summary>
    Notify,

    /// <summary>
    /// TODO (doc 12, owner decision): a hard due date would block Ready to In progress with a structured refusal. Not implemented.
    /// The scanner refuses to run in this mode rather than act as if it were Notify.
    /// </summary>
    Hard,
}
