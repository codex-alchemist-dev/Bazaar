namespace Bazaar.Server.Security.Policy;

/// <summary>
/// Decides whether one tool call may run (doc 15, "Enforcement"). <c>ToolDispatcher</c> is the only caller; a prompt
/// may describe these rules but never changes them.
/// </summary>
public interface IToolPolicyEngine
{
    /// <summary>Returns Allow, RequireApproval or Deny with the reason. An allowed call that has a quota counts against it.</summary>
    PolicyDecision Evaluate(DispatchContext context);
}
