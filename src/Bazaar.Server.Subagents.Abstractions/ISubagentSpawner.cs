namespace Bazaar.Server.Subagents;

/// <summary>Starts child sessions under the limits of doc 17 "Review flow and subagents" and doc 06 (depth, concurrency).</summary>
public interface ISubagentSpawner
{
    /// <summary>
    /// Validates the spec against the parent, waits for a slot (cancellable), writes the spawn record, then starts the child.
    /// Throws <see cref="SubagentRejectedException"/> when refused; nothing is started in that case.
    /// </summary>
    Task<SubagentHandle> SpawnAsync(ParentContext parent, SubagentSpec spec, CancellationToken cancellationToken);
}
