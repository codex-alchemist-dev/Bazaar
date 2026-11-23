namespace Bazaar.Kodo.Testing;

/// <summary>The experiment kinds of the chaos catalogue (doc 46 section 17.3).</summary>
public enum ChaosFaultKind
{
    KillModule,
    SlowDependency,
    DropFrames,
    FillDisk,
    SkewClock,
    PauseGateway,

    /// <summary>Corrupt one slot copy. Sandbox only, always.</summary>
    CorruptSlotCopy,
}

/// <summary>
/// Chaos mode options (doc 46 section 17.3). Chaos is off by default and has no kinds by default: nothing can run until an
/// operator enables it and names the kinds. The numeric limits are PROPOSED (17.5); only the 10 second abort bound is from the doc.
/// </summary>
public sealed record ChaosOptions
{
    /// <summary>The abort bound of section 17.3. Settings may tighten it, never loosen it.</summary>
    public static TimeSpan AbortDeadlineCeiling { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Master switch. False unless the operator turned it on.</summary>
    public bool Enabled { get; init; }

    /// <summary>The allow-list of kinds. Empty means nothing may run.</summary>
    public IReadOnlySet<ChaosFaultKind> AllowedKinds { get; init; } = new HashSet<ChaosFaultKind>();

    /// <summary>Seed of the random choices, so a run can be replayed.</summary>
    public int Seed { get; init; }

    /// <summary>PROPOSED: the shortest wait between experiments.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>PROPOSED: the longest wait between experiments.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>PROPOSED: the blast-radius limit as a percent of the sandbox (0 to 100).</summary>
    public int MaxIntensityPercent { get; init; } = 25;

    /// <summary>PROPOSED: an experiment is aborted when it runs longer than this.</summary>
    public TimeSpan MaxExperimentDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long after an abort condition the experiment must have stopped (section 17.3: 10 seconds).</summary>
    public TimeSpan AbortDeadline { get; init; } = AbortDeadlineCeiling;

    /// <summary>PROPOSED: how often the scheduler checks the gate and the duration while an experiment runs.</summary>
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    public void Validate()
    {
        if (MinInterval <= TimeSpan.Zero || MaxInterval < MinInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(MinInterval), "The intervals must be positive and the maximum at least the minimum.");
        }

        if (MaxIntensityPercent is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIntensityPercent), MaxIntensityPercent, "The intensity limit must be 1 to 100.");
        }

        if (MaxExperimentDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxExperimentDuration), "The experiment duration limit must be positive.");
        }

        if (AbortDeadline <= TimeSpan.Zero || AbortDeadline > AbortDeadlineCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(AbortDeadline), AbortDeadline, "The abort deadline must be positive and at most 10 seconds (section 17.3).");
        }

        if (WatchInterval <= TimeSpan.Zero || WatchInterval >= AbortDeadline)
        {
            throw new ArgumentOutOfRangeException(nameof(WatchInterval), WatchInterval, "The watch interval must be positive and below the abort deadline.");
        }
    }
}

/// <summary>
/// What the gate says now. <see cref="Authorized"/> is true only after a caller holding <c>kodo.chaos.enable</c> passed step-up
/// (the role check lives outside the core). <see cref="IncidentOpen"/> blocks chaos entirely (section 17.3).
/// </summary>
public readonly record struct ChaosGateState(bool Authorized, bool IncidentOpen)
{
    public bool Open => Authorized && !IncidentOpen;
}

/// <summary>Supplies the gate state. Read before every decision and while an experiment runs.</summary>
public interface IChaosGate
{
    ChaosGateState Current { get; }
}

/// <summary>One experiment to run in the sandbox.</summary>
public sealed record ChaosExperiment(
    int Sequence,
    ChaosFaultKind Kind,
    int IntensityPercent,
    TimeSpan MaxDuration,
    DateTimeOffset StartedAt);

/// <summary>How an experiment ended.</summary>
public enum ChaosOutcome
{
    Completed,

    /// <summary>The abort condition fired and the sandbox stopped within the deadline.</summary>
    Aborted,

    /// <summary>The abort condition fired and the sandbox did not stop within the deadline. A finding.</summary>
    AbortTimedOut,

    /// <summary>The sandbox threw.</summary>
    Failed,
}

/// <summary>What the sandbox saw (section 17.3).</summary>
/// <param name="Recovery">Time to recover, or null when it never did.</param>
/// <param name="RunbookSucceeded">Whether a runbook ran and succeeded, or null when none was needed.</param>
public sealed record ChaosExperimentResult(
    ChaosExperiment Experiment,
    ChaosOutcome Outcome,
    bool HypothesisHeld,
    TimeSpan? Recovery,
    bool DataLossInvariantHeld,
    bool ExpectedAlertFired,
    bool UnexpectedCriticalAlert,
    bool? RunbookSucceeded);

/// <summary>
/// Runs one experiment against the sandbox copy of the company. Doc 17 sandboxes do not exist yet, so the core only sees this
/// interface. An implementation must stop promptly once <c>cancellationToken</c> is cancelled.
/// </summary>
public interface IChaosSandbox
{
    /// <summary>True only for a real sandbox copy on separate storage with fake data. The scheduler refuses anything else.</summary>
    bool IsSandbox { get; }

    Task<ChaosExperimentResult> RunAsync(ChaosExperiment experiment, CancellationToken cancellationToken);
}

/// <summary>The scheduler's decision for now.</summary>
public enum ChaosDecisionKind
{
    Disabled,
    GateClosed,
    IncidentOpen,
    NotSandbox,
    NoAllowedKinds,
    Busy,
    NotDue,
    Start,
}

public sealed record ChaosDecision(ChaosDecisionKind Kind, ChaosExperiment? Experiment, DateTimeOffset? NextDueAt);
