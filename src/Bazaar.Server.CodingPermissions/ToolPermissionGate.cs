using Bazaar.Contracts.Coding;

namespace Bazaar.Server.CodingPermissions;

/// <summary>
/// The coding layer of the permission gate (doc 17, "Permissions"). Class tiers, taint, approvals and quotas belong to
/// Server's generic policy behind <see cref="IGenericToolPolicy"/>; this type adds the doc 17 rules: agent/* push rules,
/// default-branch denial, repo-config refusal, and a trace row before every return. Fails closed.
/// </summary>
public sealed class ToolPermissionGate : IIdentifiedToolPermissionGate
{
    private const string _agentBranchPrefix = "agent/";

    private readonly IGenericToolPolicy _policy;
    private readonly IToolCallTrace _trace;
    private readonly IAgentFreeze _freeze;
    private readonly CodingPermissionOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the gate. The options are validated here.</summary>
    public ToolPermissionGate(
        IGenericToolPolicy policy,
        IToolCallTrace trace,
        IAgentFreeze freeze,
        CodingPermissionOptions options,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(freeze);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _policy = policy;
        _trace = trace;
        _freeze = freeze;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<PermissionDecision> EvaluateAsync(ToolCallContext context, CancellationToken cancellationToken) =>
        (await EvaluateWithIdAsync(context, cancellationToken).ConfigureAwait(false)).Decision;

    /// <inheritdoc />
    public async ValueTask<GateEvaluation> EvaluateWithIdAsync(ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var started = _time.GetUtcNow();

        PermissionDecision decision;
        try
        {
            decision = await DecideAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            decision = PermissionDecision.Deny($"Permission check failed closed: {ex.GetType().Name}.");
        }

        ToolCallRecord record;

        // The row is written before the decision is returned. If it cannot be written, nothing runs.
        try
        {
            record = ToRecord(context, decision, started);
            await _trace.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new GateEvaluation(PermissionDecision.Deny("The tool-call trace could not be written, so the call is refused."), null);
        }

        return new GateEvaluation(decision, record.Id);
    }

    private static bool AlwaysNeedsApproval(ToolCapabilityClass c) =>
        c is ToolCapabilityClass.Merge or ToolCapabilityClass.Deploy or ToolCapabilityClass.Spend
            or ToolCapabilityClass.CreateAccount or ToolCapabilityClass.GrantScope;

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static ToolCallRecord ToRecord(ToolCallContext context, PermissionDecision decision, DateTimeOffset at) =>
        new(
            Guid.NewGuid(),
            context.SessionId ?? string.Empty,
            context.Tool ?? string.Empty,
            context.CapabilityClass.ToString(),
            context.SourceTrust ?? string.Empty,
            context.Taint != SessionTaint.None,

            // An allowed call has not run yet and the table is append-only, so its outcome is recorded as unknown.
            decision.Outcome == PermissionOutcome.Allow ? ToolCallStatus.Unknown : ToolCallStatus.Refused,
            null,
            context.ArgsSha256 ?? string.Empty,
            at,
            decision.Outcome == PermissionOutcome.Allow ? null : at);

    private async ValueTask<PermissionDecision> DecideAsync(ToolCallContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.SessionId)
            || string.IsNullOrWhiteSpace(context.Tool) || context.SourceTrust is null || !IsSha256Hex(context.ArgsSha256 ?? string.Empty))
        {
            return PermissionDecision.Deny("The call context is incomplete or the args hash is not a SHA-256.");
        }

        if (!Enum.IsDefined(context.CapabilityClass) || !Enum.IsDefined(context.Taint))
        {
            return PermissionDecision.Deny("Unknown capability class or taint.");
        }

        if (RepoSuppliedConfigGuard.Check(context) is { } guardReason)
        {
            return PermissionDecision.Deny(guardReason);
        }

        if (await _freeze.IsFrozenAsync(context.AgentId, ct).ConfigureAwait(false))
        {
            return PermissionDecision.Deny("The agent is frozen.");
        }

        var cls = context.CapabilityClass;
        if (cls == ToolCapabilityClass.Push && PushBranchDenial(context.TargetBranch) is { } branchReason)
        {
            return PermissionDecision.Deny(branchReason);
        }

        var generic = _policy.Evaluate(context);
        switch (generic.Outcome)
        {
            case PermissionOutcome.Deny:
                return generic.RateLimited ? await FreezeOnBreachAsync(context, ct).ConfigureAwait(false) : PermissionDecision.Deny(generic.Reason);
            case PermissionOutcome.RequireApproval:
                return PermissionDecision.RequireApproval(generic.Reason, ScopeFor(context));
            case PermissionOutcome.Allow:
                break;
            default:
                return PermissionDecision.Deny("Unknown policy outcome.");
        }

        // Doc 17 floors that hold even if the generic table is looser: these classes always need a per-action approval,
        // and a push needs one while hard-tainted (or always, with harness.push_always_approve).
        var needsGrant = AlwaysNeedsApproval(cls)
            || (cls == ToolCapabilityClass.Push && (context.Taint == SessionTaint.Hard || _options.PushAlwaysApprove));
        if (needsGrant && !generic.ApprovedByGrant)
        {
            var why = AlwaysNeedsApproval(cls)
                ? $"Class {cls} always needs an approval for each action."
                : context.Taint == SessionTaint.Hard
                    ? "A hard-tainted session needs an approval to push."
                    : "Every push needs an approval (harness.push_always_approve).";
            return PermissionDecision.RequireApproval(why, ScopeFor(context));
        }

        return PermissionDecision.Allow(generic.Reason);
    }

    private static ApprovalScope ScopeFor(ToolCallContext context) =>
        new(
            $"{context.CapabilityClass} via {context.Tool}"
            + (context.TargetBranch is { } b ? $" on {b}" : string.Empty)
            + $" in session {context.SessionId}",
            PerAction: true);

    private async ValueTask<PermissionDecision> FreezeOnBreachAsync(ToolCallContext context, CancellationToken ct)
    {
        const string Why = "Rate cap breached.";
        try
        {
            await _freeze.FreezeAsync(context.AgentId, Why, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return PermissionDecision.Deny(Why + " Freezing the agent also failed.");
        }

        return PermissionDecision.Deny(Why + " The agent is frozen and the owner is notified.");
    }

    private string? PushBranchDenial(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return "A push must name its target branch.";
        }

        if (string.Equals(branch, _options.DefaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            return "Agents never push to the default branch.";
        }

        if (!branch.StartsWith(_agentBranchPrefix, StringComparison.Ordinal)
            || branch.Length == _agentBranchPrefix.Length
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Any(char.IsWhiteSpace))
        {
            return "Agents push only to agent/* branches.";
        }

        return null;
    }
}
