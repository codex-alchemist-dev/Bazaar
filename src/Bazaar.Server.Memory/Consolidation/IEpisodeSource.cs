namespace Bazaar.Server.Memory.Consolidation;

/// <summary>Supplies an agent's episodic notes to the consolidation runner.</summary>
public interface IEpisodeSource
{
    /// <summary>Returns the agent's episodic notes not yet consumed by a completed run, in a stable order.</summary>
    Task<IReadOnlyList<EpisodeNote>> ReadSinceLastRunAsync(string agentId, CancellationToken ct);

    /// <summary>
    /// Marks episodes as consumed. The runner calls this only after a run completed (and committed, when it had
    /// operations), so a failed, capped or malformed run leaves its episodes for the next run.
    /// </summary>
    Task MarkConsumedAsync(string agentId, IReadOnlyList<string> episodeIds, CancellationToken ct);
}
