using System.Collections.Immutable;
using System.Text.Json;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using ModuleSessionTaint = Bazaar.Server.CodingPermissions.SessionTaint;
using ServerSessionTaint = Bazaar.Server.Security.Taint.SessionTaint;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Binds the coding permission gate's <see cref="IGenericToolPolicy"/> port to Server's <see cref="IToolPolicyEngine"/>
/// (doc 15 tiers, taint, approvals and quotas). It only maps shapes. Anything it cannot map is denied, never allowed.
/// </summary>
public sealed class ToolPolicyEngineAdapter : IGenericToolPolicy
{
    private readonly IToolPolicyEngine _engine;
    private readonly CapabilityRegistry _registry;
    private readonly TimeProvider _time;

    /// <summary>Creates the adapter.</summary>
    /// <param name="engine">Server's policy engine.</param>
    /// <param name="registry">The registry the engine uses; the adapter checks the module's class claim against it.</param>
    /// <param name="time">The clock for the engine's quota windows and grant expiry.</param>
    public ToolPolicyEngineAdapter(IToolPolicyEngine engine, CapabilityRegistry registry, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(registry);
        _engine = engine;
        _registry = registry;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public GenericPolicyDecision Evaluate(ToolCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Guid.TryParseExact(context.SessionId, "D", out var sessionId))
        {
            return Deny("The session id is not a GUID.");
        }

        if (context.Args.ValueKind == JsonValueKind.Undefined)
        {
            return Deny("The call's arguments were not supplied.");
        }

        if (!TryMapRole(context.Role, out var role) || !TryMapTaint(context.Taint, out var taint))
        {
            return Deny("Unknown session role or taint.");
        }

        // The module's class label must agree with the registry, or a mislabelled call could pass a gate rule keyed on the class.
        if (_registry.TryGet(context.Tool, out var tool) && (int)tool.Class != (int)context.CapabilityClass)
        {
            return Deny("The tool's capability class does not match the registry.");
        }

        var grant = context.Grant is { } g
            ? new VerifiedApproval(g.ApprovalId, context.AgentId, context.Tool, g.ArgsHash, g.ExpiresAt)
            : null;

        var dispatch = new DispatchContext(
            context.AgentId,
            sessionId,
            role,
            context.Tool,
            Platform: null,
            Target: context.TargetBranch ?? context.TargetPath,
            context.Args,
            taint,
            _time.GetUtcNow(),
            grant);

        var decision = _engine.Evaluate(dispatch);
        var outcome = decision.Outcome switch
        {
            PolicyOutcome.Allow => PermissionOutcome.Allow,
            PolicyOutcome.RequireApproval => PermissionOutcome.RequireApproval,
            _ => PermissionOutcome.Deny,
        };

        return new GenericPolicyDecision(
            outcome,
            decision.Reason.ToString(),
            ApprovedByGrant: outcome == PermissionOutcome.Allow && decision.Reason == PolicyReason.ApprovedByGrant,
            RateLimited: decision.Reason == PolicyReason.RateLimited);
    }

    private static GenericPolicyDecision Deny(string reason) => new(PermissionOutcome.Deny, reason);

    private static bool TryMapRole(ToolSessionRole role, out SessionRoles mapped)
    {
        mapped = role switch
        {
            ToolSessionRole.Reader => SessionRoles.Reader,
            ToolSessionRole.Actor => SessionRoles.Actor,
            _ => SessionRoles.None,
        };
        return mapped != SessionRoles.None;
    }

    // The module carries the taint level only; the engine's source list stays empty, so a grant bound to a hash that
    // included sources does not match here and the call is asked for approval again (fail closed).
    private static bool TryMapTaint(ModuleSessionTaint taint, out ServerSessionTaint mapped)
    {
        TaintState? state = taint switch
        {
            ModuleSessionTaint.None => TaintState.Clean,
            ModuleSessionTaint.Soft => TaintState.Soft,
            ModuleSessionTaint.Hard => TaintState.Hard,
            _ => null,
        };
        mapped = state is { } s ? new ServerSessionTaint(s, ImmutableArray<TaintSource>.Empty) : ServerSessionTaint.Clean;
        return state is not null;
    }
}
