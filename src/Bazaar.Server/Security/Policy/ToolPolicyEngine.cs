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
}

/// <summary>
/// The pure decision table for tool calls. The only side effect is counting an allowed call against its quota.
/// Fails closed: an unknown tool, an unknown role or a malformed context is denied.
/// </summary>
public sealed class ToolPolicyEngine(CapabilityRegistry registry, IRateBucketStore rateBuckets, PolicyOptions options) : IToolPolicyEngine
{
    private readonly CapabilityRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IRateBucketStore _rateBuckets = rateBuckets ?? throw new ArgumentNullException(nameof(rateBuckets));
    private readonly PolicyOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public PolicyDecision Evaluate(DispatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.Tool))
        {
            return new PolicyDecision(PolicyOutcome.Deny, PolicyReason.UnknownTool);
        }

        if (!_registry.TryGet(context.Tool, out var tool))
        {
            return new PolicyDecision(PolicyOutcome.Deny, PolicyReason.UnknownTool);
        }

        byte[] dispatchHash;
        try
        {
            dispatchHash = DispatchHash.Compute(context);
        }
        catch (ArgumentException)
        {
            return new PolicyDecision(PolicyOutcome.Deny, PolicyReason.MalformedArguments);
        }

        if (!IsSingleRole(context.Role) || (tool.Roles & context.Role) == 0)
        {
            return new PolicyDecision(PolicyOutcome.Deny, PolicyReason.RoleNotPermitted);
        }

        // Hard taint is a fixed gate (doc 15): it escalates Tier 1 to Tier 2 and blocks Tier 2 without a per-action approval.
        var hard = context.Taint.State == TaintState.Hard;
        var effective = tool.Tier == ToolTier.AutonomousWithNotice && hard ? ToolTier.OwnerApproves : tool.Tier;

        var decision = effective switch
        {
            ToolTier.Denied => new PolicyDecision(PolicyOutcome.Deny, PolicyReason.TierDenied),
            ToolTier.HumanPerforms => new PolicyDecision(PolicyOutcome.Deny, PolicyReason.HumanPerformsAction),
            ToolTier.Autonomous => new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous),
            ToolTier.AutonomousWithNotice => new PolicyDecision(PolicyOutcome.Allow, PolicyReason.NoticeRequired),
            ToolTier.OwnerApproves => DecideTier2(context, dispatchHash, hard),
            _ => new PolicyDecision(PolicyOutcome.Deny, PolicyReason.TierDenied),
        };

        if (decision.Outcome != PolicyOutcome.Allow || tool.Rate is null)
        {
            return decision;
        }

        var check = _rateBuckets.TryConsume(BucketKey(context), tool.Rate, context.Now);
        return check.Allowed ? decision : new PolicyDecision(PolicyOutcome.Deny, PolicyReason.RateLimited);
    }

    private PolicyDecision DecideTier2(DispatchContext context, byte[] dispatchHash, bool hard)
    {
        if (context.Grant is { } grant && GrantCovers(grant, context, dispatchHash))
        {
            return new PolicyDecision(PolicyOutcome.Allow, PolicyReason.ApprovedByGrant);
        }

        if (hard)
        {
            return new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.HardTaintTier2);
        }

        return _options.Tier2WhenUntainted == Tier2WhenUntainted.Always
            ? new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.OwnerApprovalRequired)
            : new PolicyDecision(PolicyOutcome.Allow, PolicyReason.UntaintedTier2Allowed);
    }

    /// <summary>
    /// A grant covers one call only: same agent, same tool, same dispatch hash (so the same args, target and taint), and not expired.
    /// The approval service checks these too; the engine repeats them so a bad grant cannot pass by a caller's mistake.
    /// </summary>
    private static bool GrantCovers(VerifiedApproval grant, DispatchContext context, byte[] dispatchHash) =>
        grant.AgentId == context.AgentId
        && grant.Tool == context.Tool
        && grant.ExpiresAt > context.Now
        && grant.ArgsHash.AsSpan().SequenceEqual(dispatchHash);

    private static bool IsSingleRole(SessionRoles role) => role is SessionRoles.Reader or SessionRoles.Actor;

    private static string BucketKey(DispatchContext context) =>
        $"tool|{context.AgentId}|{context.Platform ?? "-"}|{context.Tool}";
}
