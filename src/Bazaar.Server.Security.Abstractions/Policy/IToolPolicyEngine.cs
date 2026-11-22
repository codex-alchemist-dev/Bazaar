using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Policy;

/// <summary>
/// Decides whether one tool call may run (doc 15, "Enforcement"). <c>ToolDispatcher</c> is the only caller; a prompt
/// may describe these rules but never changes them.
/// </summary>
public interface IToolPolicyEngine
{
    /// <summary>Returns Allow, RequireApproval or Deny with the reason. An allowed call that has a quota counts against it.</summary>
    PolicyDecision Evaluate(DispatchContext context);

    /// <summary>
    /// The session's taint after the tool's result reaches it (doc 15, "Reads for modules"). A tool that declares
    /// <see cref="ToolResultTaint.Hard"/> raises the session to <see cref="TaintState.Hard"/> with the result recorded as an
    /// external source; any other tool leaves the taint as it was. Taint never decreases.
    /// </summary>
    /// <remarks>
    /// This default knows no tool declarations, so it fails closed and treats every result as hard-tainting. An engine with a
    /// registry overrides it.
    /// </remarks>
    /// <param name="context">The context the call was evaluated with; <see cref="DispatchContext.Taint"/> is the taint before the result.</param>
    /// <param name="resultId">Id of the tool result, recorded as the taint source.</param>
    SessionTaint TaintAfterResult(DispatchContext context, Guid resultId)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TaintRules.Apply(context.Taint, new TaintInput(resultId, ContentTrust.External), TaintPolicy.Default);
    }
}
