using System.Globalization;
using System.Text;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Subagents;
using ModuleRejected = Bazaar.Server.Subagents.SubagentRejectedException;
using ModuleResult = Bazaar.Server.Subagents.SubagentResult;
using RuntimeRejected = Bazaar.Server.Agents.Turns.Subagents.SubagentRejectedException;

namespace Bazaar.Server.Host.Coding;

/// <summary>What the runtime needs to know about a parent turn that the module's call does not carry.</summary>
/// <param name="AgentId">The agent that owns the parent turn.</param>
/// <param name="SessionId">The parent's session; the child inherits it so taint propagates.</param>
/// <param name="ParentHolder">The parent's lease holder.</param>
/// <param name="ParentToolAllowlist">The parent's own tool allowlist, which the runtime checks the child against.</param>
/// <param name="ParentStepCap">The parent's step cap, or null for the runtime's ceiling.</param>
/// <param name="ParentTriggerGcId">The GC that woke the parent, or null.</param>
/// <param name="ParentTriggerGcSeq">The trigger message sequence, paired with <paramref name="ParentTriggerGcId"/>.</param>
public sealed record RuntimeSubagentParent(
    Guid AgentId,
    Guid SessionId,
    string ParentHolder,
    IReadOnlyList<string> ParentToolAllowlist,
    int? ParentStepCap = null,
    string? ParentTriggerGcId = null,
    long? ParentTriggerGcSeq = null);

/// <summary>Host lookup of a running parent turn by id. Returns null when the turn is not running, which refuses the spawn.</summary>
public interface IRuntimeSubagentParentDirectory
{
    /// <summary>Finds the parent turn.</summary>
    ValueTask<RuntimeSubagentParent?> FindAsync(Guid parentTurnId, CancellationToken cancellationToken);
}

/// <summary>
/// Binds the harness's <see cref="IRuntimeSubagentPort"/> to Server's <c>SubagentRunner</c> (doc 06, "Subagents"). Ids are
/// GUIDs on the runtime side and strings on the module side; the module's parent id is the parent turn id. The runtime's
/// own refusal becomes <see cref="SubagentRejection.RuntimeLimit"/>. Nothing here relaxes a limit.
/// </summary>
public sealed class RuntimeSubagentAdapter : IRuntimeSubagentPort
{
    private readonly SubagentRunner _runner;
    private readonly IRuntimeSubagentParentDirectory _parents;

    /// <summary>Creates the adapter.</summary>
    public RuntimeSubagentAdapter(SubagentRunner runner, IRuntimeSubagentParentDirectory parents)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(parents);
        _runner = runner;
        _parents = parents;
    }

    /// <inheritdoc />
    public async Task<ModuleResult> RunAsync(RuntimeSubagentCall invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!Guid.TryParseExact(invocation.ParentId, "D", out var parentTurnId))
        {
            throw new ModuleRejected(SubagentRejection.InvalidSpec, "The parent id is not a turn id.");
        }

        var parent = await _parents.FindAsync(parentTurnId, cancellationToken).ConfigureAwait(false)
            ?? throw new ModuleRejected(SubagentRejection.InvalidSpec, "The parent turn is not running.");

        var request = new SubagentRequest(
            parent.AgentId,
            parent.SessionId,
            parentTurnId,
            invocation.ParentDepth,
            parent.ParentToolAllowlist,
            parent.ParentHolder,
            PromptOf(invocation),
            invocation.Tools.Order(StringComparer.Ordinal).ToArray(),
            invocation.ModelId,
            parent.ParentStepCap,
            parent.ParentTriggerGcId,
            parent.ParentTriggerGcSeq);

        Bazaar.Server.Agents.Turns.Subagents.SubagentResult result;
        try
        {
            result = await _runner.SpawnAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeRejected ex)
        {
            throw new ModuleRejected(SubagentRejection.RuntimeLimit, $"The runtime refused the subagent ({ex.ReasonCode}).", ex);
        }

        // The runtime reports steps only; token counts stay with the turn tables, so they are zero here.
        return new ModuleResult(
            StatusOf(result.Reason),
            new UntrustedText(result.Summary ?? string.Empty),
            new SubagentUsage(0, 0, result.StepCount),
            result.ChildTurnId.ToString("D", CultureInfo.InvariantCulture));
    }

    // The runtime has no field for references, so they travel as lines after the task, which is all the child gets.
    private static string PromptOf(RuntimeSubagentCall call)
    {
        if (call.References.Count == 0)
        {
            return call.Task;
        }

        var text = new StringBuilder(call.Task).Append("\n\nReferences:");
        foreach (var reference in call.References)
        {
            text.Append("\n- ").Append(reference);
        }

        return text.ToString();
    }

    private static SubagentStatus StatusOf(TurnEndReason reason) => reason switch
    {
        TurnEndReason.FinalMessage => SubagentStatus.Completed,
        TurnEndReason.Interrupted => SubagentStatus.Cancelled,
        _ => SubagentStatus.Failed,
    };
}
