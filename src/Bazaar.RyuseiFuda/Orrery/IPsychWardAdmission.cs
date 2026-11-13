namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// One guard trip that admits a trigger's target agents to the Psych Ward (doc 38, Loop and runaway safeguards; doc 26, section 1).
/// </summary>
/// <param name="TriggerId">The trigger the trip paused.</param>
/// <param name="Trip">The refusal that tripped the guard (chain depth, the daily cap or the refusal streak).</param>
/// <param name="GuardKind">The <c>guard_event.kind</c> written for the trip.</param>
/// <param name="AgentIds">The trigger's target agents, the agents involved in the trip.</param>
public sealed record PsychWardAdmission(Guid TriggerId, RyuseiRefusal Trip, string GuardKind, IReadOnlyList<Guid> AgentIds);

/// <summary>
/// The Psych Ward hook for RyuseiFuda guard trips (doc 38, "the Psych Ward is the place for a loop trip"). The scheduler calls it
/// after the trip's transaction commits. The Psych Ward (doc 26) is not built on the server yet, so the default is
/// <see cref="NoOpPsychWardAdmission"/>; the trip is still recorded in <c>guard_event</c> and the trigger stays paused.
/// </summary>
public interface IPsychWardAdmission
{
    /// <summary>Admits the agents in <paramref name="admission"/> to the Psych Ward. Called once per trip.</summary>
    Task AdmitAsync(PsychWardAdmission admission, CancellationToken ct);
}

/// <summary>The default <see cref="IPsychWardAdmission"/>. It admits nobody, until the Psych Ward is built (doc 26).</summary>
public sealed class NoOpPsychWardAdmission : IPsychWardAdmission
{
    /// <inheritdoc />
    public Task AdmitAsync(PsychWardAdmission admission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return Task.CompletedTask;
    }
}
