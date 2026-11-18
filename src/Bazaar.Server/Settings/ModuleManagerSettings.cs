using Bazaar.Server.Modules.LineMove;
using Bazaar.Server.Modules.Updates;

namespace Bazaar.Server.Settings;

/// <summary>
/// The module manager's operator settings: the <c>deps.update.*</c> keys (doc 42 section 10), the <c>releases.ring.*</c> and
/// <c>releases.gate.*</c> keys (doc 49 section 6) and <c>lineMove.drillMaxAgeDays</c> (doc 42 section 10.3). All install scope and
/// operator-changed. Each default is a safe shipped default for a value the design left open (CLAUDE.md configurability rule).
/// Nothing here is a security floor: the beta-never-auto rule and the GA-major-needs-approval rule are DECIDED invariants and
/// have no key.
/// </summary>
public static class ModuleManagerSettings
{
    public static readonly SettingKey<int> DeferWarnDays = new(
        DeferAging.DeferWarnDaysSettingKey, DeferAging.DefaultDeferWarnDays, SettingScope.Install, SettingRole.Operator,
        "Days a deferred update may wait before the module manager and Kodo show it as aging.",
        validate: Between(7, 365, "deps.update.deferWarnDays"));

    public static readonly SettingKey<bool> AutoSecurityPatches = new(
        "deps.update.autoSecurityPatches", true, SettingScope.Install, SettingRole.Operator,
        "Apply security patches on a line in use automatically. Off means every patch waits for the owner.");

    public static readonly SettingKey<int> RingCanaryPercent = new(
        "releases.ring.canaryPercent", 1, SettingScope.Install, SettingRole.Operator,
        "Share of installs offered a release in the canary ring, in percent.",
        validate: Percent("releases.ring.canaryPercent"));

    public static readonly SettingKey<int> RingEarlyPercent = new(
        "releases.ring.earlyPercent", 10, SettingScope.Install, SettingRole.Operator,
        "Share of installs offered a release in the early ring, in percent. Must not be below the canary share (see RingOrderError).",
        validate: Percent("releases.ring.earlyPercent"));

    public static readonly SettingKey<int> RingBroadPercent = new(
        "releases.ring.broadPercent", 100, SettingScope.Install, SettingRole.Operator,
        "Share of installs offered a release in the broad ring, in percent. Must not be below the early share (see RingOrderError).",
        validate: Percent("releases.ring.broadPercent"));

    public static readonly SettingKey<int> GateMinimumSamples = new(
        "releases.gate.minimumSamples", 30, SettingScope.Install, SettingRole.Operator,
        "Health samples a ring needs before the gate may continue. Below this the gate holds.",
        validate: AtLeast(1, "releases.gate.minimumSamples"));

    public static readonly SettingKey<double> GateMaxErrorRate = new(
        "releases.gate.maxErrorRate", 0.01, SettingScope.Install, SettingRole.Operator,
        "Mean error rate above which a ring halts. A rate equal to this value does not halt.",
        validate: CheckRate);

    public static readonly SettingKey<int> LineMoveDrillMaxAgeDays = new(
        "lineMove.drillMaxAgeDays", 90, SettingScope.Install, SettingRole.Operator,
        "Days since the last restore drill before a line move's preflight refuses to start.",
        validate: AtLeast(1, "lineMove.drillMaxAgeDays"));

    public static readonly SettingKey<int> LineMoveRetryMaxAttempts = new(
        "lineMove.retry.maxAttempts", LineMoveRetryPolicy.DefaultMaxAttempts, SettingScope.Install, SettingRole.Operator,
        "Attempts a rolled-back line move gets, including the first. When every attempt rolls back, the move halts for the owner.",
        validate: Between(1, 10, "lineMove.retry.maxAttempts"));

    public static readonly SettingKey<int> LineMoveRetryInitialBackoffMinutes = new(
        "lineMove.retry.initialBackoffMinutes", (int)LineMoveRetryPolicy.Default.InitialBackoff.TotalMinutes, SettingScope.Install, SettingRole.Operator,
        "Minutes to wait before the second attempt. Later waits grow by the multiplier.",
        validate: Between(1, 1440, "lineMove.retry.initialBackoffMinutes"));

    public static readonly SettingKey<double> LineMoveRetryMultiplier = new(
        "lineMove.retry.multiplier", LineMoveRetryPolicy.Default.Multiplier, SettingScope.Install, SettingRole.Operator,
        "Growth of the wait between attempts. Each wait is the previous one times this value.",
        validate: CheckMultiplier);

    public static readonly SettingKey<int> LineMoveRetryMaxBackoffMinutes = new(
        "lineMove.retry.maxBackoffMinutes", (int)LineMoveRetryPolicy.Default.MaxBackoff.TotalMinutes, SettingScope.Install, SettingRole.Operator,
        "Upper bound on any single wait between attempts, in minutes. Must not be below the initial wait (see RetryOrderError).",
        validate: Between(1, 10080, "lineMove.retry.maxBackoffMinutes"));

    /// <summary>Every module manager key, in the order the design lists them.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        DeferWarnDays,
        AutoSecurityPatches,
        RingCanaryPercent,
        RingEarlyPercent,
        RingBroadPercent,
        GateMinimumSamples,
        GateMaxErrorRate,
        LineMoveDrillMaxAgeDays,
        LineMoveRetryMaxAttempts,
        LineMoveRetryInitialBackoffMinutes,
        LineMoveRetryMultiplier,
        LineMoveRetryMaxBackoffMinutes,
    ];

    /// <summary>
    /// The cross-setting rule for the rings: canary &lt;= early &lt;= broad. The registry validates one key at a time, so the
    /// rollout code must call this with the three stored values before it uses them. Returns null when the order holds.
    /// </summary>
    public static string? RingOrderError(int canaryPercent, int earlyPercent, int broadPercent)
    {
        if (canaryPercent > earlyPercent)
            return $"releases.ring.canaryPercent ({canaryPercent}) must not exceed releases.ring.earlyPercent ({earlyPercent}).";
        if (earlyPercent > broadPercent)
            return $"releases.ring.earlyPercent ({earlyPercent}) must not exceed releases.ring.broadPercent ({broadPercent}).";
        return null;
    }

    /// <summary>
    /// The cross-setting rule for the retry waits: the maximum wait must not be shorter than the initial wait. Returns null when it holds.
    /// </summary>
    public static string? RetryOrderError(int initialBackoffMinutes, int maxBackoffMinutes) =>
        maxBackoffMinutes < initialBackoffMinutes
            ? $"lineMove.retry.maxBackoffMinutes ({maxBackoffMinutes}) must not be below lineMove.retry.initialBackoffMinutes ({initialBackoffMinutes})."
            : null;

    /// <summary>
    /// Builds the retry policy from the four stored values. Throws <see cref="ArgumentException"/> when the cross-setting order fails,
    /// and the policy's own range check throws for any other bad value, so a broken setting never reaches a running move.
    /// </summary>
    public static LineMoveRetryPolicy RetryPolicyFrom(int maxAttempts, int initialBackoffMinutes, double multiplier, int maxBackoffMinutes)
    {
        if (RetryOrderError(initialBackoffMinutes, maxBackoffMinutes) is { } error)
            throw new ArgumentException(error, nameof(maxBackoffMinutes));

        return new LineMoveRetryPolicy(
            maxAttempts,
            TimeSpan.FromMinutes(initialBackoffMinutes),
            multiplier,
            TimeSpan.FromMinutes(maxBackoffMinutes)).Validated();
    }

    private static string? CheckMultiplier(double multiplier) =>
        double.IsFinite(multiplier) && multiplier is >= 1 and <= 10
            ? null
            : $"'lineMove.retry.multiplier' must be from 1 to 10; got {multiplier}.";

    private static Func<int, string?> AtLeast(int min, string name) =>
        value => value >= min ? null : $"'{name}' must be at least {min}; got {value}.";

    private static Func<int, string?> Between(int min, int max, string name) =>
        value => value >= min && value <= max ? null : $"'{name}' must be from {min} to {max}; got {value}.";

    private static Func<int, string?> Percent(string name) =>
        value => value is >= 0 and <= 100 ? null : $"'{name}' must be from 0 to 100; got {value}.";

    private static string? CheckRate(double rate) =>
        double.IsFinite(rate) && rate is >= 0 and <= 1 ? null : $"'releases.gate.maxErrorRate' must be from 0 to 1; got {rate}.";
}
