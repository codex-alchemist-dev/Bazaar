namespace Bazaar.Kodo.Evaluator;

/// <summary>
/// The state of the single recovery action, if any (doc 46 section 8.2). A verify step that still
/// finds red rules is read from the observation after the action is <see cref="Idle"/>.
/// </summary>
public enum RecoveryStatus
{
    /// <summary>No recovery action is running or pending.</summary>
    Idle,

    /// <summary>A recovery action (restart, slot switch, runbook, failover, restore) is running.</summary>
    Running,

    /// <summary>The action failed or timed out.</summary>
    Failed,
}

/// <summary>
/// Aggregated probe and recovery facts for one evaluation (doc 46 sections 8.1, 8.2 and 9). The probe
/// runtime builds it, applying grace periods and unknown budgets before the evaluator sees it.
/// </summary>
/// <remarks>
/// <see cref="AllRulesGreen"/> is derived, so a caller cannot report green while a rule is not.
/// <c>unknown</c> never counts as green (section 9).
/// </remarks>
public sealed record KodoObservation
{
    /// <summary>Any critical probe is red (section 9).</summary>
    public bool CriticalRed { get; init; }

    /// <summary>A critical probe is <c>unknown</c> within its unknown budget (section 8.1).</summary>
    public bool CriticalUnknown { get; init; }

    /// <summary>An integrity or module contract failure is open (sections 6.4 and 10).</summary>
    public bool IntegrityOrContractFailure { get; init; }

    /// <summary>A crash loop is active (section 7.1).</summary>
    public bool CrashLoop { get; init; }

    /// <summary>Number of non-critical probes that are red past their grace period (section 8.2).</summary>
    public int NonCriticalRedBeyondGrace { get; init; }

    /// <summary>A non-critical probe is amber past its grace period (section 8.2).</summary>
    public bool NonCriticalAmberBeyondGrace { get; init; }

    /// <summary>A non-critical probe is red or amber but still inside its grace period (section 8.2).</summary>
    public bool NonCriticalWithinGrace { get; init; }

    /// <summary>An SLO is burning above its warning threshold (section 12.3).</summary>
    public bool SloWarningBurn { get; init; }

    /// <summary>An SLO is breached (section 12.3). Used only for the degraded-to-failing rule.</summary>
    public bool SloBreach { get; init; }

    /// <summary>The recovery action state (section 8.2).</summary>
    public RecoveryStatus Recovery { get; init; } = RecoveryStatus.Idle;

    /// <summary>Incident mode is active (section 16.3). Shown as <see cref="KodoState.Frozen"/>.</summary>
    public bool IncidentActive { get; init; }

    /// <summary>Every rule is green. Only this state starts the stability window (section 8.2).</summary>
    public bool AllRulesGreen =>
        !CriticalRed
        && !CriticalUnknown
        && !IntegrityOrContractFailure
        && !CrashLoop
        && NonCriticalRedBeyondGrace == 0
        && !NonCriticalAmberBeyondGrace
        && !NonCriticalWithinGrace
        && !SloWarningBurn
        && !SloBreach;
}
