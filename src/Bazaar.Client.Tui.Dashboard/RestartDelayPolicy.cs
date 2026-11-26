namespace Bazaar.Client.Tui.Dashboard;

/// <summary>
/// Bounded restart delays for a crashed resident TUI (doc 58 section 3a), like the server's: <c>host.service.restart_delays_minutes</c>
/// (default 1, 1, 5) and <c>host.service.failure_reset_hours</c> (default 24) from doc 03. The service manager relaunches the TUI; this
/// policy says how long to wait before the Nth crash in a row is relaunched. After the last listed delay the delay stays there, so it
/// never grows without bound. A crash older than the reset window no longer counts. The policy is a pure function of the crash times
/// from <see cref="IOpsCrashJournal"/>, so a crash that killed the process still counts on the next start.
/// </summary>
public sealed class RestartDelayPolicy
{
    public static readonly IReadOnlyList<int> DefaultDelaysMinutes = [1, 1, 5];

    public static readonly TimeSpan DefaultFailureReset = TimeSpan.FromHours(24);

    private readonly int[] _delaysMinutes;
    private readonly TimeSpan _failureReset;

    /// <param name="delaysMinutes">The configured delays. Entries at or below zero are dropped; an empty or all-invalid list falls back to the default.</param>
    /// <param name="failureReset">The window after which earlier crashes are forgotten. At or below zero falls back to the default.</param>
    public RestartDelayPolicy(IReadOnlyList<int>? delaysMinutes = null, TimeSpan? failureReset = null)
    {
        var valid = (delaysMinutes ?? []).Where(minutes => minutes > 0).ToArray();
        _delaysMinutes = valid.Length > 0 ? valid : [.. DefaultDelaysMinutes];
        _failureReset = failureReset is { } reset && reset > TimeSpan.Zero ? reset : DefaultFailureReset;
    }

    public IReadOnlyList<int> DelaysMinutes => _delaysMinutes;

    public TimeSpan FailureReset => _failureReset;

    /// <summary>The delay before relaunching after a crash, given every recorded crash time including the one that just happened.</summary>
    public TimeSpan DelayAfter(IReadOnlyList<DateTimeOffset> crashTimes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(crashTimes);
        var recent = crashTimes.Count(time => time <= now && now - time <= _failureReset);
        var index = Math.Clamp(recent - 1, 0, _delaysMinutes.Length - 1);
        return TimeSpan.FromMinutes(_delaysMinutes[index]);
    }
}
