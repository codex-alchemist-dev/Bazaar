using Bazaar.Contracts.Gc;

namespace Bazaar.Server.PsychWard.Abstractions;

/// <summary>
/// Writes ward and vent happenings into group chats (doc 26: admission opens a <c>ward</c> GC with a <c>checkin</c> entry;
/// vent drafts live in the agent's private <c>vent</c> GC). The journal runs after the state change is saved, so a failing
/// journal never undoes a restriction; the exception reaches the caller. Every write is idempotent.
/// </summary>
public interface IWardJournal
{
    /// <summary>Opens the stay's ward GC and notes the admission.</summary>
    Task AdmittedAsync(WardStay stay, CancellationToken cancellationToken);

    /// <summary>Writes the check-in entry in the stay's ward GC.</summary>
    Task CheckedInAsync(WardStay stay, CancellationToken cancellationToken);

    /// <summary>Notes the discharge in the stay's ward GC. The GC and the failure record stay.</summary>
    Task DischargedAsync(WardStay stay, CancellationToken cancellationToken);

    /// <summary>Notes a written vent in the agent's private vent GC. Carries references and status, not the text.</summary>
    Task VentWrittenAsync(VentDraft vent, CancellationToken cancellationToken);

    /// <summary>Notes a published vent in the agent's private vent GC.</summary>
    Task VentPublishedAsync(VentDraft vent, CancellationToken cancellationToken);
}

/// <summary>Looks up the principals the journal needs. The host supplies it from the owner and agent directories.</summary>
public interface IWardPrincipalDirectory
{
    /// <summary>The owner, or null when no owner exists yet.</summary>
    Task<GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken);

    /// <summary>An agent's principal, or null when the agent is unknown.</summary>
    Task<GcPrincipal?> GetAgentAsync(Guid agentId, CancellationToken cancellationToken);
}

/// <summary>Supplies the owner principal. Identity (or the first-run seed) provides it; the module never guesses an owner.</summary>
public interface IWardOwnerSource
{
    /// <summary>The owner, or null when no owner exists yet.</summary>
    Task<GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken);
}
