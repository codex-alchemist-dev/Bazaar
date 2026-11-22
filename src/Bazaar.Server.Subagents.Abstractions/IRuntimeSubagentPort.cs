namespace Bazaar.Server.Subagents;

/// <summary>
/// Everything the runtime gets for one child. There is deliberately no field for the parent's transcript:
/// a child gets the task, explicit references and its limits, nothing else.
/// </summary>
/// <param name="SubagentId">The subagent id.</param>
/// <param name="ChainId">The parent chain.</param>
/// <param name="ParentId">The parent session.</param>
/// <param name="ParentDepth">The parent's depth; the runtime computes the child's depth from it (doc 06).</param>
/// <param name="Task">The task text.</param>
/// <param name="Tools">The final tool allowlist (the spawn tool is already removed at the harness depth limit).</param>
/// <param name="ModelId">The model route, from the parent's whitelist.</param>
/// <param name="Worktree">The worktree the child may use, if any.</param>
/// <param name="References">Explicitly attached references.</param>
public sealed record RuntimeSubagentCall(
    string SubagentId,
    string ChainId,
    string ParentId,
    int ParentDepth,
    string Task,
    IReadOnlySet<string> Tools,
    string ModelId,
    WorktreeLease? Worktree,
    IReadOnlyList<string> References);

/// <summary>
/// Port to the agent runtime's subagent runner (doc 06 "Subagents"; <c>Bazaar.Server</c> <c>SubagentRunner</c>). The runtime owns the
/// depth limit, the concurrency limit and the step cap and enforces them itself; a host adapter binds this port to it. The harness
/// only tightens depth and adds policy in front. An adapter throws <see cref="SubagentRejectedException"/> with
/// <see cref="SubagentRejection.RuntimeLimit"/> when the runtime refuses.
/// </summary>
public interface IRuntimeSubagentPort
{
    /// <summary>Runs the child to its end with exactly the given tools and model, and returns its result.</summary>
    Task<SubagentResult> RunAsync(RuntimeSubagentCall invocation, CancellationToken cancellationToken);
}
