namespace Bazaar.Server.Security.Quarantine;

/// <summary>Pauses and resumes both VMs of an agent (doc 16; kill switch).</summary>
public interface IVmPauser
{
    /// <summary>Pauses the agent's Windows and Linux VMs. Must be idempotent.</summary>
    Task PauseAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// Resumes the agent's Windows and Linux VMs that <see cref="PauseAsync"/> paused. Must be idempotent, and must throw if a
    /// VM could not be resumed. Callers authorize before calling; this method does not check who asks.
    /// </summary>
    Task ResumeAsync(string agentId, CancellationToken ct = default);
}
