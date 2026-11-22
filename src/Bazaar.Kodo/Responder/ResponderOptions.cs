namespace Bazaar.Kodo.Responder;

/// <summary>A daily quiet window in UTC minutes since midnight. It may wrap past midnight. Critical alerts are never quieted.</summary>
/// <param name="StartMinuteUtc">First quiet minute, 0 to 1439.</param>
/// <param name="EndMinuteUtc">First minute that is not quiet, 0 to 1439. Equal to the start means no quiet time.</param>
public sealed record QuietWindow(int StartMinuteUtc, int EndMinuteUtc)
{
    /// <summary>True when <paramref name="at"/> falls inside the window.</summary>
    public bool Contains(DateTimeOffset at)
    {
        if (StartMinuteUtc == EndMinuteUtc)
        {
            return false;
        }

        DateTimeOffset utc = at.ToUniversalTime();
        int minute = (utc.Hour * 60) + utc.Minute;
        return StartMinuteUtc < EndMinuteUtc
            ? minute >= StartMinuteUtc && minute < EndMinuteUtc
            : minute >= StartMinuteUtc || minute < EndMinuteUtc;
    }
}

/// <summary>
/// Alert routing settings (doc 46 sections 13.4, 16.1 and 20). Every default is the conservative one and is PROPOSED where the doc is silent
/// (section 16.8). The shipped quiet window is off: an operator who opts in still never quiets a critical alert.
/// </summary>
public sealed record AlertOptions
{
    /// <summary>The same fingerprint inside this window is a duplicate. Default 30 minutes.</summary>
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Alerts of one category inside this window of the first one are grouped. Default 60 seconds.</summary>
    public TimeSpan GroupWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The rate-limit window for each channel. Default 10 minutes.</summary>
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Deliveries one channel may take per <see cref="RateWindow"/> for non-critical alerts. Default 6.</summary>
    public int MaxPerChannel { get; init; } = 6;

    /// <summary>Deliveries one channel may take per <see cref="RateWindow"/> for critical alerts, counted apart. Default 20.</summary>
    public int MaxCriticalPerChannel { get; init; } = 20;

    /// <summary>Unacknowledged critical alerts escalate after this long (setting <c>kodo.alert.escalation_min.critical</c>). Default 15 minutes.</summary>
    public TimeSpan CriticalEscalation { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The quiet window, or null for none (the default).</summary>
    public QuietWindow? Quiet { get; init; }

    /// <summary>Consecutive send failures that open a channel's breaker (section 7.4). Default 5.</summary>
    public int BreakerFailures { get; init; } = 5;

    /// <summary>How long a channel's breaker stays open. Default 60 seconds.</summary>
    public TimeSpan BreakerCooldown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The most fingerprints and groups tracked. When full and nothing has expired, the router delivers instead of suppressing.</summary>
    public int MaxTracked { get; init; } = 1024;

    /// <summary>Category first segment to the role that owns it. Anything not listed belongs to <c>owner</c>.</summary>
    public IReadOnlyDictionary<string, string> CategoryOwners { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The channels per severity. The shipped table is <see cref="DefaultChannels"/>.</summary>
    public IReadOnlyDictionary<AlertSeverity, IReadOnlyList<AlertChannelKind>> Channels { get; init; } = DefaultChannels;

    /// <summary>The shipped channel table. Info stays in the client; critical wakes devices and goes to every outward channel.</summary>
    public static IReadOnlyDictionary<AlertSeverity, IReadOnlyList<AlertChannelKind>> DefaultChannels { get; } =
        new Dictionary<AlertSeverity, IReadOnlyList<AlertChannelKind>>
        {
            [AlertSeverity.Info] = [AlertChannelKind.InClient],
            [AlertSeverity.Warning] = [AlertChannelKind.InClient, AlertChannelKind.Webhook],
            [AlertSeverity.Critical] = [AlertChannelKind.InClient, AlertChannelKind.Push, AlertChannelKind.Email, AlertChannelKind.Webhook],
        };

    /// <summary>Checks every value. A bad value throws, so a bad setting cannot silently mean "never alert".</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DedupeWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(GroupWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RateWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPerChannel, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCriticalPerChannel, MaxPerChannel);
        ArgumentOutOfRangeException.ThrowIfLessThan(CriticalEscalation, TimeSpan.FromMinutes(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CriticalEscalation, TimeSpan.FromHours(24));
        ArgumentOutOfRangeException.ThrowIfLessThan(BreakerFailures, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(BreakerCooldown, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTracked, 1);
        ArgumentNullException.ThrowIfNull(CategoryOwners);
        ArgumentNullException.ThrowIfNull(Channels);

        if (Quiet is { } quiet
            && (quiet.StartMinuteUtc is < 0 or > 1439 || quiet.EndMinuteUtc is < 0 or > 1439))
        {
            throw new ArgumentOutOfRangeException(nameof(Quiet), quiet, "Quiet minutes are 0 to 1439.");
        }

        foreach (AlertSeverity severity in Enum.GetValues<AlertSeverity>())
        {
            if (!Channels.TryGetValue(severity, out IReadOnlyList<AlertChannelKind>? list) || list is null)
            {
                throw new ArgumentException($"No channels for {severity}.", nameof(Channels));
            }

            if (severity == AlertSeverity.Critical && !list.Contains(AlertChannelKind.InClient))
            {
                throw new ArgumentException("Critical alerts always reach the client.", nameof(Channels));
            }
        }
    }
}

/// <summary>
/// Runbook engine settings (doc 46 sections 16.2 and 20). Auto runs are off for every runbook until the operator names it. All values
/// are PROPOSED (section 16.8).
/// </summary>
public sealed record RunbookOptions
{
    /// <summary>The most attempts any step gets, whatever the runbook asks. Default 3.</summary>
    public int MaxAttemptsCap { get; init; } = 3;

    /// <summary>The delay before the first retry; it doubles. Default 5 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The cap on a retry delay. Default 5 minutes.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A run waiting for approval longer than this stops as needs-human. Time out never approves. Default 4 hours.</summary>
    public TimeSpan ApprovalWait { get; init; } = TimeSpan.FromHours(4);

    /// <summary>The runbook ids that may run without approval. Empty by default, so nothing runs unattended until the operator opts in.</summary>
    public IReadOnlySet<string> AutoRunbookIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Checks every value.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttemptsCap, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxAttemptsCap, 5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetryBaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryMaxDelay, RetryBaseDelay);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ApprovalWait, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(AutoRunbookIds);
    }
}
