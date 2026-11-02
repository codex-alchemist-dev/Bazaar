namespace Bazaar.Server.Fun.Ward;

/// <summary>The outcome of an admission or discharge request.</summary>
/// <param name="Accepted">True when the transition happened.</param>
/// <param name="Stay">The stay after the request; unchanged when refused.</param>
/// <param name="Refusal">Why the request was refused, or null when accepted.</param>
public sealed record WardDecision(bool Accepted, WardStay? Stay, string? Refusal)
{
    /// <summary>A refused request with a reason.</summary>
    public static WardDecision Refuse(string reason, WardStay? stay) => new(false, stay, reason);

    /// <summary>An accepted request.</summary>
    public static WardDecision Accept(WardStay stay) => new(true, stay, null);
}

/// <summary>
/// Pure transitions for one agent's ward stays (doc 26, section 1). It does no I/O: the caller stores the returned
/// <see cref="WardStay"/> and writes the <c>ward</c> GC entries. Time comes from the caller.
/// </summary>
public sealed class WardStateMachine
{
    private readonly WardSettings _settings;

    /// <summary>Creates the machine. Settings are validated once here.</summary>
    /// <param name="settings">The ward settings.</param>
    public WardStateMachine(WardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Validate();
        _settings = settings;
    }

    /// <summary>
    /// Admits an agent. An owner referral or the owner's own admission always works. An automatic trigger is refused
    /// when <c>ward.admission_mode</c> is owner-only, and no second stay opens while one is active.
    /// </summary>
    /// <param name="agentId">The agent to admit.</param>
    /// <param name="trigger">What admits the agent.</param>
    /// <param name="now">The admission time.</param>
    /// <param name="current">The agent's latest stay, or null when it has never been admitted.</param>
    public WardDecision Admit(string agentId, WardTrigger trigger, DateTimeOffset now, WardStay? current)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        if (current is { IsAdmitted: true })
        {
            return WardDecision.Refuse("The agent is already admitted.", current);
        }

        var ownerDriven = trigger is WardTrigger.OwnerReferral;
        if (!ownerDriven && _settings.AdmissionMode == WardAdmissionMode.OwnerOnly)
        {
            return WardDecision.Refuse("Admission is owner-only; the owner must admit this agent.", current);
        }

        return WardDecision.Accept(new WardStay(agentId, trigger, now));
    }

    /// <summary>Records the check-in for the active stay. The check-in grants nothing.</summary>
    public static WardDecision RecordCheckIn(WardCheckIn checkIn, WardStay? current)
    {
        ArgumentNullException.ThrowIfNull(checkIn);

        if (current is not { IsAdmitted: true })
        {
            return WardDecision.Refuse("There is no active stay to check in to.", current);
        }

        return WardDecision.Accept(current with { CheckIn = checkIn });
    }

    /// <summary>
    /// Discharges the active stay. It needs the minimum stay to have passed and a discharger the settings allow.
    /// The owner can always discharge; a reviewer may discharge only under
    /// <see cref="WardDischargeBy.OwnerOrCleanReviewer"/>, and only when clean (no tie to the failure and no taint).
    /// </summary>
    /// <param name="current">The active stay.</param>
    /// <param name="now">The discharge time.</param>
    /// <param name="dischargerIsOwner">True when the owner discharges.</param>
    /// <param name="reviewerIsClean">True when a non-owner reviewer has no tie to the failure and no taint.</param>
    public WardDecision Discharge(WardStay? current, DateTimeOffset now, bool dischargerIsOwner, bool reviewerIsClean)
    {
        if (current is not { IsAdmitted: true })
        {
            return WardDecision.Refuse("There is no active stay to discharge.", current);
        }

        if (!dischargerIsOwner)
        {
            if (_settings.DischargeBy == WardDischargeBy.Owner)
            {
                return WardDecision.Refuse("Only the owner may discharge this agent.", current);
            }

            if (!reviewerIsClean)
            {
                return WardDecision.Refuse("A reviewer must be clean (no tie to the failure, no taint) to discharge.", current);
            }
        }

        var earliest = current.AdmittedAt.AddMinutes(_settings.MinStayMinutes);
        if (now < earliest)
        {
            return WardDecision.Refuse($"The minimum stay has not passed (until {earliest:O}).", current);
        }

        return WardDecision.Accept(current with { DischargedAt = now });
    }

    /// <summary>What an agent may not do given its latest stay (doc 26: pushes, merges, public posts refused while admitted).</summary>
    public static WardRestrictions RestrictionsFor(WardStay? current) =>
        current is { IsAdmitted: true } ? WardRestrictions.Admitted : WardRestrictions.None;
}
