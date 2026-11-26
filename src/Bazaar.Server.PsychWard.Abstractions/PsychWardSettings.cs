namespace Bazaar.Server.PsychWard.Abstractions;

/// <summary><c>ward.admission_mode</c> values.</summary>
public enum WardAdmissionMode
{
    /// <summary>Any deterministic trigger admits the agent.</summary>
    Automatic,

    /// <summary>Only the owner admits.</summary>
    OwnerOnly,
}

/// <summary><c>ward.discharge_by</c> values.</summary>
public enum WardDischargeBy
{
    /// <summary>Only the owner discharges.</summary>
    Owner,

    /// <summary>The owner, or a reviewer with no tie to the failure and no taint.</summary>
    OwnerOrCleanReviewer,
}

/// <summary><c>ward.curfew_behaviour</c> values: what an admission that arrives during the agent's curfew does.</summary>
public enum WardCurfewBehaviour
{
    /// <summary>The stay is recorded now and counts from the agent's next shift start. Restrictions apply at once.</summary>
    QueueNextShift,

    /// <summary>The stay begins now, curfew or not.</summary>
    AdmitNow,
}

/// <summary>The <c>ward.*</c> and <c>vent.*</c> settings from doc 26 with the shipped defaults.</summary>
public sealed class PsychWardSettings
{
    /// <summary>The internal default vent target.</summary>
    public const string InternalGeneralGc = "internal_general_gc";

    /// <summary><c>ward.min_stay_minutes</c>.</summary>
    public int MinStayMinutes { get; init; } = 60;

    /// <summary><c>ward.admission_mode</c>.</summary>
    public WardAdmissionMode AdmissionMode { get; init; } = WardAdmissionMode.Automatic;

    /// <summary><c>ward.discharge_by</c>.</summary>
    public WardDischargeBy DischargeBy { get; init; } = WardDischargeBy.OwnerOrCleanReviewer;

    /// <summary><c>ward.curfew_behaviour</c>.</summary>
    public WardCurfewBehaviour CurfewBehaviour { get; init; } = WardCurfewBehaviour.QueueNextShift;

    /// <summary>
    /// <c>ward.repeated_failure_count</c>: consecutive failed outcomes on one tag that admit an agent. Doc 26 and doc 11 say three.
    /// </summary>
    public int RepeatedFailureCount { get; init; } = 3;

    /// <summary>
    /// <c>ward.denied_call_threshold</c>: denied calls in the window the caller measures that count as an anomaly. Doc 26 and
    /// doc 15 name "repeated denials" without a number, so this is an operator setting; the shipped default is 5 [PROPOSED].
    /// </summary>
    public int DeniedCallThreshold { get; init; } = 5;

    /// <summary><c>vent.required_before_card</c>.</summary>
    public bool VentRequiredBeforeCard { get; init; } = true;

    /// <summary>External vent targets the operator configured (doc 36). Empty by default.</summary>
    public IReadOnlySet<string> VentOperatorTargets { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Checks the values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The minimum stay is negative, or a count is below one.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MinStayMinutes);
        ArgumentOutOfRangeException.ThrowIfLessThan(RepeatedFailureCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(DeniedCallThreshold, 1);
    }
}
