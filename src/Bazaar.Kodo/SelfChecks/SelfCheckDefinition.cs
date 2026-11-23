using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.SelfChecks;

/// <summary>The scheduled self-checks of doc 46 section 17.2.</summary>
public enum SelfCheckKind
{
    /// <summary>Daily scan of <c>kodo-data</c> and <c>kodo-evidence</c> against recorded hashes. A mismatch is an integrity finding.</summary>
    ChecksumSweep,

    /// <summary>Weekly restore of the latest backup into a scratch database, then schema, sampled group chat and Vault index checks.</summary>
    RestoreBackup,

    /// <summary>Monthly decryption of a recovery bundle in the sandbox with a test-only key path.</summary>
    KeyRecoveryRehearsal,

    /// <summary>Quarterly controlled primary stop, scripted failover and failback in a sandbox copy. Only when standby is configured.</summary>
    FailoverDrill,
}

/// <summary>
/// One self-check: what it is, when it is due, how long it may run and how long a missing or non-green result is tolerated (section 17.2).
/// </summary>
public sealed class SelfCheckDefinition
{
    /// <summary>The longest a single run may be given.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(6);

    /// <summary>Creates a definition. Throws <see cref="ArgumentException"/> for an id that is not an identifier, or a timeout or grace out of range.</summary>
    /// <param name="id">The identifier, for example <c>selfcheck.restore</c>.</param>
    /// <param name="kind">Which check it is.</param>
    /// <param name="schedule">When it is due.</param>
    /// <param name="timeout">How long one run may take before it counts as timed out (1 second to 6 hours).</param>
    /// <param name="grace">How long a result may be missing, stale or not green before it escalates from amber or within-grace to red or beyond-grace.</param>
    /// <param name="enabled">False leaves the check out entirely, for example the failover drill when no standby is configured.</param>
    public SelfCheckDefinition(string id, SelfCheckKind kind, SelfCheckSchedule schedule, TimeSpan timeout, TimeSpan grace, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (string.IsNullOrEmpty(id) || !KodoExportPatterns.IdentifierValue().IsMatch(id))
        {
            throw new ArgumentException("The id must be an identifier.", nameof(id));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a self-check kind.");
        }

        if (timeout < TimeSpan.FromSeconds(1) || timeout > MaxTimeout)
        {
            throw new ArgumentException("The timeout must be from 1 second to 6 hours.", nameof(timeout));
        }

        if (grace < TimeSpan.Zero || grace > TimeSpan.FromDays(60))
        {
            throw new ArgumentException("The grace must be from zero to 60 days.", nameof(grace));
        }

        Id = id;
        Kind = kind;
        Schedule = schedule;
        Timeout = timeout;
        Grace = grace;
        Enabled = enabled;
    }

    /// <summary>The identifier.</summary>
    public string Id { get; }

    /// <summary>Which check it is.</summary>
    public SelfCheckKind Kind { get; }

    /// <summary>When it is due.</summary>
    public SelfCheckSchedule Schedule { get; }

    /// <summary>How long one run may take.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>How long a missing, stale or non-green result is tolerated before it escalates.</summary>
    public TimeSpan Grace { get; }

    /// <summary>False leaves the check out of scheduling and out of the observation.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// The four checks with the cadences of section 17.2 (daily, weekly, 30 days, 90 days) from one anchor. The failover drill is
    /// enabled only when <paramref name="standbyConfigured"/> is true.
    /// </summary>
    public static IReadOnlyList<SelfCheckDefinition> Defaults(DateTimeOffset anchor, bool standbyConfigured) =>
    [
        new("selfcheck.checksum", SelfCheckKind.ChecksumSweep, new SelfCheckSchedule(anchor, TimeSpan.FromDays(1)), TimeSpan.FromHours(2), TimeSpan.FromHours(6)),
        new("selfcheck.restore", SelfCheckKind.RestoreBackup, new SelfCheckSchedule(anchor, TimeSpan.FromDays(7)), TimeSpan.FromHours(6), TimeSpan.FromDays(1)),
        new("selfcheck.key_recovery", SelfCheckKind.KeyRecoveryRehearsal, new SelfCheckSchedule(anchor, TimeSpan.FromDays(30)), TimeSpan.FromHours(1), TimeSpan.FromDays(3)),
        new("selfcheck.failover_drill", SelfCheckKind.FailoverDrill, new SelfCheckSchedule(anchor, TimeSpan.FromDays(90)), TimeSpan.FromHours(2), TimeSpan.FromDays(7), standbyConfigured),
    ];
}
