namespace Bazaar.Kodo.Sentinel;

/// <summary>The delay the sentinel waits before restarting Kodo, with the exit it answers and the restart total.</summary>
/// <param name="Delay">How long to wait before the next start.</param>
/// <param name="ConsecutiveExits">Unstable exits in a row, counting this one. Starts at 1 after a stable run.</param>
/// <param name="RestartCount">Restarts decided since the sentinel started, counting this one.</param>
public sealed record RestartDecision(TimeSpan Delay, int ConsecutiveExits, int RestartCount);

/// <summary>
/// The sentinel's restart backoff (doc 46 section 7.1, setting <c>kodo.supervise.restart_backoff_s</c>). Each exit of Kodo
/// waits for the next delay in the schedule. The last delay is the cap, so every later exit waits that long. A run that lasts
/// at least the stable period clears the count, so the next exit starts again at the first delay. Pure: the caller passes the
/// times it observed.
/// </summary>
public sealed class RestartBackoff
{
    /// <summary>The shipped schedule in seconds, from doc 46 section 20.</summary>
    public static IReadOnlyList<int> DefaultScheduleSeconds { get; } = [1, 2, 5, 15, 60, 300];

    /// <summary>PROPOSED: a run this long counts as stable and clears the backoff. Doc 46 states no stable period.</summary>
    public static TimeSpan DefaultStablePeriod { get; } = TimeSpan.FromMinutes(10);

    private readonly TimeSpan[] _schedule;
    private readonly TimeSpan _stablePeriod;
    private int _consecutiveExits;

    /// <summary>Creates a backoff over <paramref name="scheduleSeconds"/>, which must be non-empty, at least 1, and not decreasing.</summary>
    /// <exception cref="ArgumentException">The schedule is empty, has a value below 1, or decreases.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stable period is not positive.</exception>
    public RestartBackoff(IEnumerable<int> scheduleSeconds, TimeSpan stablePeriod)
    {
        ArgumentNullException.ThrowIfNull(scheduleSeconds);
        if (stablePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stablePeriod), "The stable period must be positive.");
        }

        int[] seconds = scheduleSeconds.ToArray();
        if (seconds.Length == 0)
        {
            throw new ArgumentException("The restart schedule needs at least one delay.", nameof(scheduleSeconds));
        }

        for (int i = 0; i < seconds.Length; i++)
        {
            if (seconds[i] < 1)
            {
                throw new ArgumentException("Every restart delay must be at least 1 second.", nameof(scheduleSeconds));
            }

            if (i > 0 && seconds[i] < seconds[i - 1])
            {
                throw new ArgumentException("The restart schedule must not decrease, because its last value is the cap.", nameof(scheduleSeconds));
            }
        }

        _schedule = seconds.Select(value => TimeSpan.FromSeconds(value)).ToArray();
        _stablePeriod = stablePeriod;
    }

    /// <summary>The longest delay the schedule can give, which is its last value.</summary>
    public TimeSpan Cap => _schedule[^1];

    /// <summary>Unstable exits since the last stable run.</summary>
    public int ConsecutiveExits => _consecutiveExits;

    /// <summary>Restarts decided since the sentinel started.</summary>
    public int RestartCount { get; private set; }

    /// <summary>
    /// Records one exit of Kodo that ran from <paramref name="startedAt"/> to <paramref name="exitedAt"/> and returns the delay
    /// before the next start. A run at least as long as the stable period clears the count before the delay is chosen.
    /// </summary>
    public RestartDecision RecordExit(DateTimeOffset startedAt, DateTimeOffset exitedAt)
    {
        if (exitedAt - startedAt >= _stablePeriod)
        {
            _consecutiveExits = 0;
        }

        int index = Math.Min(_consecutiveExits, _schedule.Length - 1);
        _consecutiveExits++;
        RestartCount++;
        return new RestartDecision(_schedule[index], _consecutiveExits, RestartCount);
    }
}
