namespace Bazaar.Kodo.Rings;

/// <summary>The rule ids a halt reason names. Fixed, lowercase, identifier-shaped.</summary>
public static class RingRuleIds
{
    public const string CrashRate = "ring.crash_rate";
    public const string ErrorCodeRate = "ring.error_code_rate";
    public const string StreamFailureRate = "ring.stream_failure_rate";
    public const string ModuleContract = "ring.module_contract";
    public const string CriticalProbeRed = "ring.critical_probe_red";
    public const string SloBurn = "ring.slo_burn";
    public const string SelfTest = "ring.self_test";
}

/// <summary>The codes explaining why a check could not call a ring healthy. Fixed strings, never free text.</summary>
public static class RingInsufficientCodes
{
    public const string InvalidSignals = "invalid_signals";
    public const string WindowShort = "window_short";
    public const string TooFewDevices = "too_few_devices";
    public const string TooFewSessions = "too_few_sessions";
    public const string BaselineMissing = "baseline_missing";
    public const string BaselineTooSmall = "baseline_too_small";
    public const string RingNotLive = "ring_not_live";
    public const string WindowPredatesLive = "window_predates_live";
}

/// <summary>The outcome of checking one ring.</summary>
public enum RingVerdict
{
    /// <summary>Every rule was evaluated on enough data and none tripped.</summary>
    Healthy,

    /// <summary>At least one rule tripped. The ring halts.</summary>
    Halt,

    /// <summary>The data was too thin or too short to say. Never healthy: the ring stays where it is until the owner acts (section 16.6).</summary>
    InsufficientData,
}

/// <summary>One tripped rule, as numbers and a fixed rule id.</summary>
/// <param name="RuleId">The rule, one of <see cref="RingRuleIds"/>.</param>
/// <param name="Observed">The observed value (a rate per 1,000, a burn rate, or a count).</param>
/// <param name="Threshold">The threshold it met or passed.</param>
/// <param name="Events">The events behind the value.</param>
/// <param name="Denominator">The sessions or attempts behind the value, or 0 for a count rule.</param>
public sealed record HaltReason(string RuleId, decimal Observed, decimal Threshold, long Events, long Denominator);

/// <summary>The result of evaluating a cohort against its baseline.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Reasons">The tripped rules. Empty unless the verdict is <see cref="RingVerdict.Halt"/>.</param>
/// <param name="InsufficientCodes">Why data was insufficient, as fixed codes. May be non-empty with a halt too, when the rate rules lacked data.</param>
/// <param name="NewCriticalModules">The critical modules whose contract failures are new against the baseline (component ids as given).</param>
public sealed record RingEvaluation(
    RingVerdict Verdict,
    IReadOnlyList<HaltReason> Reasons,
    IReadOnlyList<string> InsufficientCodes,
    IReadOnlyList<string> NewCriticalModules);

/// <summary>
/// Halt thresholds (doc 46 sections 16.6 and 20). The documented defaults are the loosest values a setting may take: it may make a halt
/// easier to trigger (a lower multiple, fewer events, a shorter window) and may demand more data before a ring counts as healthy (more
/// devices, more sessions), never the reverse. Values for rules the doc leaves open are PROPOSED (section 16.9).
/// </summary>
public sealed record RingHaltOptions
{
    /// <summary>Crash rate halts at this multiple of the baseline. Default 2 (doc 46 16.6); a setting may only lower it.</summary>
    public decimal CrashMultiple { get; init; } = 2m;

    /// <summary>Crash events needed before the crash rule may halt. Default 20 (doc 46 16.6); a setting may only lower it, down to 3.</summary>
    public int CrashMinEvents { get; init; } = 20;

    /// <summary>The crash rate per 1,000 sessions a baseline counts as at least, so a spotless baseline does not make any crash a halt. Default 1; may only be lowered.</summary>
    public decimal CrashFloorPer1000 { get; init; } = 1m;

    /// <summary>Error-code rate halts at this multiple of the baseline. PROPOSED default 2; may only be lowered.</summary>
    public decimal ErrorMultiple { get; init; } = 2m;

    /// <summary>Error events needed before the error-code rule may halt. PROPOSED default 50; may only be lowered, down to 5.</summary>
    public int ErrorMinEvents { get; init; } = 50;

    /// <summary>Baseline floor for the error-code rate per 1,000 sessions. PROPOSED default 5; may only be lowered.</summary>
    public decimal ErrorFloorPer1000 { get; init; } = 5m;

    /// <summary>Stream failure rate halts at this multiple of the baseline. PROPOSED default 2; may only be lowered.</summary>
    public decimal StreamMultiple { get; init; } = 2m;

    /// <summary>Failed attempts needed before the stream rule may halt. PROPOSED default 20; may only be lowered, down to 3.</summary>
    public int StreamMinEvents { get; init; } = 20;

    /// <summary>Baseline floor for the stream failure rate per 1,000 attempts. PROPOSED default 10; may only be lowered.</summary>
    public decimal StreamFloorPer1000 { get; init; } = 10m;

    /// <summary>The server SLO burn rate that halts a ring. PROPOSED default 6 (a fast burn, section 12.3); may only be lowered.</summary>
    public decimal SloBurnHalt { get; init; } = 6m;

    /// <summary>The window a rate must be observed over before a rate rule may halt or clear. Default 60 minutes; may only be shortened, down to 10 minutes.</summary>
    public TimeSpan ObservationWindow { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Devices needed in a cohort and in its baseline. PROPOSED default 3; may only be raised.</summary>
    public int MinDevices { get; init; } = 3;

    /// <summary>Sessions (and stream attempts) needed in a cohort and in its baseline for a rate to mean anything. PROPOSED default 500; may only be raised.</summary>
    public int MinSessions { get; init; } = 500;

    /// <summary>The loosest values: the shipped defaults.</summary>
    public static RingHaltOptions Floors { get; } = new();

    /// <summary>Checks every value against the floors. A looser or nonsensical value throws, so a setting cannot loosen the rules.</summary>
    public void Validate()
    {
        RingHaltOptions floor = Floors;
        CheckMultiple(CrashMultiple, floor.CrashMultiple, nameof(CrashMultiple));
        CheckMultiple(ErrorMultiple, floor.ErrorMultiple, nameof(ErrorMultiple));
        CheckMultiple(StreamMultiple, floor.StreamMultiple, nameof(StreamMultiple));
        CheckEvents(CrashMinEvents, 3, floor.CrashMinEvents, nameof(CrashMinEvents));
        CheckEvents(ErrorMinEvents, 5, floor.ErrorMinEvents, nameof(ErrorMinEvents));
        CheckEvents(StreamMinEvents, 3, floor.StreamMinEvents, nameof(StreamMinEvents));
        CheckFloorRate(CrashFloorPer1000, floor.CrashFloorPer1000, nameof(CrashFloorPer1000));
        CheckFloorRate(ErrorFloorPer1000, floor.ErrorFloorPer1000, nameof(ErrorFloorPer1000));
        CheckFloorRate(StreamFloorPer1000, floor.StreamFloorPer1000, nameof(StreamFloorPer1000));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SloBurnHalt, 1m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SloBurnHalt, floor.SloBurnHalt);
        ArgumentOutOfRangeException.ThrowIfLessThan(ObservationWindow, TimeSpan.FromMinutes(10));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ObservationWindow, floor.ObservationWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinDevices, floor.MinDevices);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinSessions, floor.MinSessions);
    }

    private static void CheckMultiple(decimal value, decimal loosest, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 1m, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, loosest, name);
    }

    private static void CheckEvents(int value, int lowest, int loosest, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, lowest, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, loosest, name);
    }

    private static void CheckFloorRate(decimal value, decimal loosest, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0m, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, loosest, name);
    }
}

/// <summary>
/// The halt-rule evaluator (doc 46 section 16.6). A pure function of a cohort, its baseline and the options. Two kinds of rule:
/// immediate rules (a new critical-module contract failure, a critical probe red on a server running the new version, a failed
/// self-test, a fast SLO burn) halt on one observation; rate rules (crash, error-code, stream failure) compare with the baseline over a
/// full observation window and need enough devices, sessions and events. A tripped rule always halts. With no tripped rule, any
/// shortage of data makes the verdict <see cref="RingVerdict.InsufficientData"/>, never healthy.
/// </summary>
public sealed class RingHaltEvaluator
{
    private readonly RingHaltOptions _options;

    /// <summary>Creates the evaluator. The options are validated against the floors.</summary>
    public RingHaltEvaluator(RingHaltOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>Evaluates a cohort. <paramref name="baseline"/> may be null when none exists yet.</summary>
    public RingEvaluation Evaluate(CohortSignals cohort, CohortSignals? baseline)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        if (!cohort.IsWellFormed)
        {
            return new RingEvaluation(RingVerdict.InsufficientData, [], [RingInsufficientCodes.InvalidSignals], []);
        }

        CohortSignals? usable = baseline is { IsWellFormed: true } && baseline.ReleaseId != cohort.ReleaseId ? baseline : null;
        List<HaltReason> reasons = [];
        List<string> insufficient = [];
        List<string> newModules = [];
        EvaluateImmediate(cohort, usable, reasons, newModules);
        EvaluateRates(cohort, usable, reasons, insufficient);

        RingVerdict verdict = reasons.Count > 0 ? RingVerdict.Halt
            : insufficient.Count > 0 ? RingVerdict.InsufficientData
            : RingVerdict.Healthy;
        return new RingEvaluation(verdict, reasons, insufficient, newModules);
    }

    private void EvaluateImmediate(CohortSignals cohort, CohortSignals? baseline, List<HaltReason> reasons, List<string> newModules)
    {
        HashSet<(string, string)> known = baseline is null
            ? []
            : baseline.ContractFailures.Select(failure => (failure.ModuleId, failure.Code)).ToHashSet();
        List<(string ModuleId, string Code)> fresh = cohort.ContractFailures
            .Where(failure => failure.Critical)
            .Select(failure => (failure.ModuleId, failure.Code))
            .Distinct()
            .Where(failure => !known.Contains(failure))
            .ToList();
        if (fresh.Count > 0)
        {
            reasons.Add(new HaltReason(RingRuleIds.ModuleContract, fresh.Count, 1, fresh.Count, 0));
            newModules.AddRange(fresh.Select(failure => failure.ModuleId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        }

        if (cohort.RedCriticalProbes.Count > 0)
        {
            reasons.Add(new HaltReason(RingRuleIds.CriticalProbeRed, cohort.RedCriticalProbes.Count, 1, cohort.RedCriticalProbes.Count, 0));
        }

        if (cohort.SelfTestsFailed > 0)
        {
            reasons.Add(new HaltReason(RingRuleIds.SelfTest, cohort.SelfTestsFailed, 1, cohort.SelfTestsFailed, cohort.SelfTestsRun));
        }

        if (cohort.SloBurnRate >= _options.SloBurnHalt)
        {
            reasons.Add(new HaltReason(RingRuleIds.SloBurn, cohort.SloBurnRate, _options.SloBurnHalt, 0, 0));
        }
    }

    private void EvaluateRates(CohortSignals cohort, CohortSignals? baseline, List<HaltReason> reasons, List<string> insufficient)
    {
        if (cohort.Window < _options.ObservationWindow)
        {
            insufficient.Add(RingInsufficientCodes.WindowShort);
        }

        if (cohort.Devices < _options.MinDevices)
        {
            insufficient.Add(RingInsufficientCodes.TooFewDevices);
        }

        if (cohort.Sessions < _options.MinSessions || cohort.StreamAttempts < _options.MinSessions)
        {
            insufficient.Add(RingInsufficientCodes.TooFewSessions);
        }

        if (baseline is null)
        {
            insufficient.Add(RingInsufficientCodes.BaselineMissing);
        }
        else if (baseline.Devices < _options.MinDevices || baseline.Sessions < _options.MinSessions
                 || baseline.StreamAttempts < _options.MinSessions)
        {
            insufficient.Add(RingInsufficientCodes.BaselineTooSmall);
        }

        if (insufficient.Count > 0)
        {
            return;
        }

        CheckRate(RingRuleIds.CrashRate, cohort.Crashes, cohort.Sessions, cohort.CrashRatePer1000, baseline!.CrashRatePer1000,
            _options.CrashMultiple, _options.CrashFloorPer1000, _options.CrashMinEvents, reasons);
        CheckRate(RingRuleIds.ErrorCodeRate, cohort.ErrorEvents, cohort.Sessions, cohort.ErrorRatePer1000, baseline.ErrorRatePer1000,
            _options.ErrorMultiple, _options.ErrorFloorPer1000, _options.ErrorMinEvents, reasons);
        CheckRate(RingRuleIds.StreamFailureRate, cohort.StreamFailures, cohort.StreamAttempts, cohort.StreamFailureRatePer1000,
            baseline.StreamFailureRatePer1000, _options.StreamMultiple, _options.StreamFloorPer1000, _options.StreamMinEvents, reasons);
    }

    private static void CheckRate(
        string ruleId, long events, long denominator, decimal observed, decimal baselineRate, decimal multiple, decimal floor, int minEvents, List<HaltReason> reasons)
    {
        // The baseline never counts as lower than the floor, so a spotless baseline does not turn one crash into a halt.
        decimal threshold = Math.Max(baselineRate, floor) * multiple;
        if (events >= minEvents && observed >= threshold)
        {
            reasons.Add(new HaltReason(ruleId, observed, threshold, events, denominator));
        }
    }
}
