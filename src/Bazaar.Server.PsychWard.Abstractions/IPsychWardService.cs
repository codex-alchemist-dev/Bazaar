namespace Bazaar.Server.PsychWard.Abstractions;

/// <summary>Stay storage. The in-memory store is the default; a Postgres store replaces it once the <c>ward_stay</c> migration lands.</summary>
public interface IWardStayStore
{
    /// <summary>The agent's latest stay, or null when it was never admitted.</summary>
    Task<WardStay?> GetLatestAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a stay by id.</summary>
    Task SaveAsync(WardStay stay, CancellationToken cancellationToken);

    /// <summary>All stays that are not discharged.</summary>
    Task<IReadOnlyList<WardStay>> ListActiveAsync(CancellationToken cancellationToken);
}

/// <summary>Vent storage, keyed by failure reference, plus the failures still waiting for a vent.</summary>
public interface IVentStore
{
    /// <summary>Returns the stored vent for the failure, or stores the candidate and returns it.</summary>
    Task<VentDraft> GetOrAddAsync(VentDraft candidate, CancellationToken cancellationToken);

    /// <summary>The vent for a failure, or null.</summary>
    Task<VentDraft?> GetAsync(string failureRef, CancellationToken cancellationToken);

    /// <summary>Replaces a stored vent.</summary>
    Task UpdateAsync(VentDraft draft, CancellationToken cancellationToken);

    /// <summary>All vents with the given status.</summary>
    Task<IReadOnlyList<VentDraft>> ListByStatusAsync(VentStatus status, CancellationToken cancellationToken);

    /// <summary>Notes a failure that still needs a vent. Idempotent.</summary>
    Task AddOpenFailureAsync(Guid agentId, string failureRef, CancellationToken cancellationToken);

    /// <summary>Clears a failure once its vent exists.</summary>
    Task ResolveFailureAsync(string failureRef, CancellationToken cancellationToken);

    /// <summary>True when the agent has a failure with no vent yet.</summary>
    Task<bool> HasOpenFailureAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>The Psych Ward (doc 26, section 1): admission, check-in, discharge and restrictions.</summary>
public interface IPsychWardService
{
    /// <summary>
    /// Admits an agent. The owner's referral always works; an automatic trigger is refused when admission is owner-only.
    /// No second stay opens while one is active.
    /// </summary>
    Task<WardOutcome> AdmitAsync(Guid agentId, WardTrigger trigger, string? detail, CancellationToken cancellationToken);

    /// <summary>Records the check-in for the active stay.</summary>
    Task<WardOutcome> CheckInAsync(Guid agentId, WardCheckIn checkIn, CancellationToken cancellationToken);

    /// <summary>Discharges the active stay after the minimum stay, by the owner or an allowed clean reviewer.</summary>
    Task<WardOutcome> DischargeAsync(Guid agentId, DischargeRequest request, CancellationToken cancellationToken);

    /// <summary>What the agent may not do right now.</summary>
    Task<WardRestrictions> GetRestrictionsAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>The owner's view: active stays and held vents. The caller checks the viewer is the owner.</summary>
    Task<WardOverview> GetOverviewAsync(CancellationToken cancellationToken);
}

/// <summary>Thrown by a stay store when a second active stay would open for one agent (another process won the race).</summary>
public sealed class WardStayConflictException(Guid agentId)
    : InvalidOperationException($"Agent {agentId} already has an active ward stay.")
{
    /// <summary>The agent that already has an active stay.</summary>
    public Guid AgentId { get; } = agentId;
}
