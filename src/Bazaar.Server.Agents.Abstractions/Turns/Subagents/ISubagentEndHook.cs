namespace Bazaar.Server.Agents.Turns.Subagents;

/// <summary>A child turn that has ended, as a hook sees it.</summary>
/// <param name="Request">The request that spawned the child.</param>
/// <param name="Result">How the child ended and its summary.</param>
public sealed record SubagentEnded(SubagentRequest Request, SubagentResult Result);

/// <summary>
/// Runs when a subagent child ends, before its result goes back to the parent (docs/design/06-agent-runtime.md "Subagents";
/// docs/design/25-company-systems-knowledge.md, Genetic Memory Transfer trigger "Subagent finishing"). A hook is best effort:
/// an exception it throws is swallowed by the runner, so a failing hook never fails the parent turn. A cancelled hook is not swallowed.
/// </summary>
public interface ISubagentEndHook
{
    /// <summary>Called once per ended child.</summary>
    Task OnSubagentEndedAsync(SubagentEnded ended, CancellationToken cancellationToken);
}
