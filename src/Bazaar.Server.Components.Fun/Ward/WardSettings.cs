namespace Bazaar.Server.Fun.Ward;

/// <summary>
/// The ward settings from doc 26 (<c>ward.*</c>), with the shipped defaults. Changes are checked by the settings layer
/// (doc 03); this type only holds and validates the values.
/// </summary>
public sealed class WardSettings
{
    /// <summary><c>ward.min_stay_minutes</c>: the shortest stay before discharge.</summary>
    public int MinStayMinutes { get; init; } = 60;

    /// <summary><c>ward.admission_mode</c>: whether automatic triggers admit, or only the owner does.</summary>
    public WardAdmissionMode AdmissionMode { get; init; } = WardAdmissionMode.Automatic;

    /// <summary><c>ward.discharge_by</c>: who may discharge an agent.</summary>
    public WardDischargeBy DischargeBy { get; init; } = WardDischargeBy.OwnerOrCleanReviewer;

    /// <summary>Checks that the minimum stay is not negative.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The minimum stay is below zero.</exception>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfNegative(MinStayMinutes);
}

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
