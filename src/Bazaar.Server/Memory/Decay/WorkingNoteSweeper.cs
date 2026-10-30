namespace Bazaar.Server.Memory.Decay;

/// <summary>A working note as the sweeper sees it: a path, whether its task is closed, and when it was last touched.</summary>
/// <param name="Path">Vault path of the note file.</param>
/// <param name="TaskClosed">True when the task the note belongs to is closed.</param>
/// <param name="LastTouchedUtc">When the note was last written or read, in UTC.</param>
public sealed record WorkingNote(string Path, bool TaskClosed, DateTimeOffset LastTouchedUtc);

/// <summary>A sweep plan: the note paths to remove, in ordinal path order, and their count. Nothing is deleted here.</summary>
/// <param name="NotePaths">Paths to sweep.</param>
public sealed record SweepPlan(IReadOnlyList<string> NotePaths)
{
    /// <summary>Number of notes in the plan.</summary>
    public int Count => NotePaths.Count;
}

/// <summary>
/// Pure working-note sweep (docs/design/08-memory-system.md, sections 1 and 8). A working note is swept only when its
/// task is closed and it was last touched more than <see cref="Ttl"/> before now. Open tasks are never swept.
/// The sweeper returns a plan; the caller performs the removal through the Vault writer.
/// </summary>
public sealed class WorkingNoteSweeper
{
    /// <summary>Default TTL (docs/design/08-memory-system.md, Settings: memory.working.ttl_days = 7).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a sweeper.</summary>
    /// <param name="timeProvider">Source of <c>now</c>. Tests pass a fake clock.</param>
    /// <param name="ttl">Time after last touch when a closed-task note is swept. Null means <see cref="DefaultTtl"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The TTL is zero or negative.</exception>
    public WorkingNoteSweeper(TimeProvider timeProvider, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var effective = ttl ?? DefaultTtl;
        if (effective <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), effective, "TTL must be positive.");
        }

        _timeProvider = timeProvider;
        Ttl = effective;
    }

    /// <summary>The TTL in use.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>
    /// Plans which notes to sweep at the current time from the injected clock. A note is planned when its task is
    /// closed and <c>now - LastTouchedUtc</c> is strictly greater than <see cref="Ttl"/>.
    /// </summary>
    /// <param name="notes">Working notes to consider.</param>
    public SweepPlan Plan(IEnumerable<WorkingNote> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var now = _timeProvider.GetUtcNow();
        var paths = notes
            .Where(note => note.TaskClosed && now - note.LastTouchedUtc > Ttl)
            .Select(note => note.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new SweepPlan(paths);
    }
}
