namespace Bazaar.Kodo.Settings;

/// <summary>
/// The Kodo settings catalogue (doc 46 section 20). Keys, defaults and floors come from the doc.
/// Where the doc states no floor for a key, the floor is the least protective edge of its range,
/// so only the range check applies, and the entry says so in its description.
/// Bool and owner- or counsel-gated settings (failover, chaos, explain, retention, anchor sinks,
/// metrics bind) are not in this catalogue yet.
/// </summary>
public static class KodoSettingKey
{
    public const string RestartBackoffSeconds = "kodo.supervise.restart_backoff_s";
    public const string CrashLoopStarts = "kodo.supervise.crash_loop.starts";
    public const string CrashLoopWindowMinutes = "kodo.supervise.crash_loop.window_min";
    public const string PoolLocalConcurrency = "kodo.probe.pool.local.concurrency";
    public const string PoolDbConcurrency = "kodo.probe.pool.db.concurrency";
    public const string PoolExternalConcurrency = "kodo.probe.pool.external.concurrency";
    public const string PoolSlowConcurrency = "kodo.probe.pool.slow.concurrency";
    public const string ProbeTimeoutMs = "kodo.probe.timeout_ms.default";
    public const string BreakerFailures = "kodo.probe.breaker.failures";
    public const string BreakerCooldownSeconds = "kodo.probe.breaker.cooldown_s";
    public const string StabilityWindowSeconds = "kodo.state.stability_window_s";
    public const string FlapLimitTransitions = "kodo.state.flap_limit.transitions";
    public const string FlapLimitWindowMinutes = "kodo.state.flap_limit.window_min";
    public const string IntegrityScanIntervalMinutes = "kodo.integrity.scan_interval_min";
    public const string IntegrityProbationMinutes = "kodo.integrity.probation_min";
    public const string JournalCheckpointRecords = "kodo.journal.checkpoint_records";
    public const string JournalCheckpointMinutes = "kodo.journal.checkpoint_minutes";
    public const string JournalSpillMaxMb = "kodo.journal.spill_max_mb";
    public const string SloServerAvailabilityTarget = "kodo.slo.server_availability.target";
    public const string AlertEscalationCriticalMinutes = "kodo.alert.escalation_min.critical";

    /// <summary>Every setting, in doc order.</summary>
    public static IReadOnlyList<KodoSettingDefinition> All { get; } =
    [
        new(RestartBackoffSeconds, KodoSettingType.IntegerList, "1,2,5,15,60,300", 1, 86400, 1, KodoFloorRule.AtLeast,
            "Backoff in seconds between sentinel restarts of Kodo (doc 46 7.1). No floor is stated; the floor is the range minimum."),
        new(CrashLoopStarts, KodoSettingType.WholeNumber, "5", 2, 50, 5, KodoFloorRule.AtMost,
            "Starts inside the crash-loop window that move Kodo to failing with cause crash_loop (doc 46 7.1)."),
        new(CrashLoopWindowMinutes, KodoSettingType.WholeNumber, "10", 1, 1440, 10, KodoFloorRule.AtLeast,
            "Length of the crash-loop window in minutes (doc 46 7.1)."),
        new(PoolLocalConcurrency, KodoSettingType.WholeNumber, "8", 1, 64, 1, KodoFloorRule.AtLeast,
            "Concurrency of the local probe pool (doc 46 7.3). No floor is stated; the floor is the range minimum."),
        new(PoolDbConcurrency, KodoSettingType.WholeNumber, "6", 1, 64, 1, KodoFloorRule.AtLeast,
            "Concurrency of the db probe pool (doc 46 7.3). No floor is stated; the floor is the range minimum."),
        new(PoolExternalConcurrency, KodoSettingType.WholeNumber, "4", 1, 64, 1, KodoFloorRule.AtLeast,
            "Concurrency of the external probe pool (doc 46 7.3). No floor is stated; the floor is the range minimum."),
        new(PoolSlowConcurrency, KodoSettingType.WholeNumber, "2", 1, 64, 1, KodoFloorRule.AtLeast,
            "Concurrency of the slow probe pool (doc 46 7.3). No floor is stated; the floor is the range minimum."),
        new(ProbeTimeoutMs, KodoSettingType.WholeNumber, "2000", 100, 60000, 60000, KodoFloorRule.AtMost,
            "Default hard timeout for a probe in milliseconds (doc 46 7.3, section 20). No floor is stated; the floor is the range maximum."),
        new(BreakerFailures, KodoSettingType.WholeNumber, "5", 1, 100, 5, KodoFloorRule.AtMost,
            "Consecutive failures that open a probe or sink breaker (doc 46 7.4)."),
        new(BreakerCooldownSeconds, KodoSettingType.WholeNumber, "60", 1, 3600, 1, KodoFloorRule.AtLeast,
            "Cooldown before a breaker goes half-open for one trial call, in seconds (doc 46 7.4). No floor is stated; the floor is the range minimum."),
        new(StabilityWindowSeconds, KodoSettingType.WholeNumber, "300", 1, 86400, 60, KodoFloorRule.AtLeast,
            "Stability window in seconds that must pass with all rules green before a state returns to healthy (doc 46 8.2, section 20). Floor 60."),
        new(FlapLimitTransitions, KodoSettingType.WholeNumber, "6", 2, 100, 6, KodoFloorRule.AtMost,
            "State transitions inside the flap window that force degraded (doc 46 8.2, section 20)."),
        new(FlapLimitWindowMinutes, KodoSettingType.WholeNumber, "30", 1, 1440, 30, KodoFloorRule.AtLeast,
            "Length of the flap window in minutes (doc 46 8.2, section 20)."),
        new(IntegrityScanIntervalMinutes, KodoSettingType.WholeNumber, "5", 1, 1440, 5, KodoFloorRule.AtMost,
            "Minutes between integrity scans (doc 46 section 20). Floor 5: scans run at least every 5 minutes."),
        new(IntegrityProbationMinutes, KodoSettingType.WholeNumber, "10", 1, 1440, 1, KodoFloorRule.AtLeast,
            "Probation in minutes for integrity findings (doc 46 section 20). No floor is stated; the floor is the range minimum."),
        new(JournalCheckpointRecords, KodoSettingType.WholeNumber, "1000", 1, 1000000, 1000000, KodoFloorRule.AtMost,
            "Journal records between checkpoints (doc 46 section 20). No floor is stated; the floor is the range maximum."),
        new(JournalCheckpointMinutes, KodoSettingType.WholeNumber, "15", 1, 1440, 1440, KodoFloorRule.AtMost,
            "Minutes between journal checkpoints (doc 46 section 20). No floor is stated; the floor is the range maximum."),
        new(JournalSpillMaxMb, KodoSettingType.WholeNumber, "512", 16, 65536, 16, KodoFloorRule.AtLeast,
            "Size bound of the journal spill file in megabytes (doc 46 7.5). No floor is stated; the floor is the range minimum."),
        new(SloServerAvailabilityTarget, KodoSettingType.Number, "99.5", 0, 100, 95, KodoFloorRule.AtLeast,
            "Target percent for company server availability (doc 46 12.3). Floor 95 percent."),
        new(AlertEscalationCriticalMinutes, KodoSettingType.WholeNumber, "15", 1, 1440, 15, KodoFloorRule.AtMost,
            "Minutes before an unacknowledged critical alert escalates (doc 46 section 20). Floor 15."),
    ];

    private static readonly Dictionary<string, KodoSettingDefinition> _byKey =
        All.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    /// <summary>Looks up a setting by key, or returns null when the key is not in the catalogue.</summary>
    public static KodoSettingDefinition? Find(string key) => _byKey.GetValueOrDefault(key);
}
