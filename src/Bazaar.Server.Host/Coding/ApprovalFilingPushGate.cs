using System.Collections.Immutable;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Host.Coding;

/// <summary>Files an owner approval request for a push the gate said needs one (doc 17, "Push approval filing").</summary>
public interface IPushApprovalRequests
{
    /// <summary>Files the request. Returns the approval id, or null when none was filed (rate limited, malformed or a store failure).</summary>
    ValueTask<Guid?> FileAsync(PushGateRequest request, Guid sessionId, ToolSessionRole role, CancellationToken cancellationToken);
}

/// <summary>Files nothing; used when no approval service is bound, so a hard-tainted push stays at "needs approval".</summary>
public sealed class NoPushApprovalRequests : IPushApprovalRequests
{
    /// <summary>The shared instance.</summary>
    public static NoPushApprovalRequests Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<Guid?> FileAsync(PushGateRequest request, Guid sessionId, ToolSessionRole role, CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(null);
}

/// <summary>
/// Files the request through <see cref="ApprovalService"/> with exactly the hash <see cref="PermissionGatePushGate.DispatchHashOf"/> checks
/// at grant lookup: tool <c>git.push</c>, target the branch, args from <see cref="PermissionGatePushGate.ArgsOf"/>, no taint sources.
/// </summary>
public sealed class ApprovalServicePushRequests(ApprovalService service, TimeProvider time) : IPushApprovalRequests
{
    private readonly ApprovalService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <inheritdoc />
    public async ValueTask<Guid?> FileAsync(PushGateRequest request, Guid sessionId, ToolSessionRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = new DispatchContext(
            request.AgentId,
            sessionId,
            role == ToolSessionRole.Reader ? SessionRoles.Reader : SessionRoles.Actor,
            PermissionGatePushGate.ToolName,
            Platform: null,
            request.Branch.Value,
            PermissionGatePushGate.ArgsOf(request),
            new Bazaar.Server.Security.Taint.SessionTaint(TaintState.Clean, ImmutableArray<TaintSource>.Empty),
            _time.GetUtcNow());
        var result = await _service.RequestAsync(context, cancellationToken).ConfigureAwait(false);
        return result.Outcome == ApprovalService.RequestOutcome.Created ? result.Request?.Id : null;
    }
}

/// <summary>
/// Wraps the push gate: when it answers "needs approval", files the owner's approval request once for that push and adds the
/// request id to the reason. A filing failure never changes the verdict, so the push stays refused.
/// </summary>
public sealed class ApprovalFilingPushGate(IPushGate inner, IPushSessionRecords sessions, IPushApprovalRequests requests) : IPushGate
{
    private readonly IPushGate _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IPushSessionRecords _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IPushApprovalRequests _requests = requests ?? throw new ArgumentNullException(nameof(requests));

    /// <inheritdoc />
    public async ValueTask<PushDecision> EvaluateAsync(PushGateRequest request, CancellationToken cancellationToken)
    {
        var decision = await _inner.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision.Verdict != PushVerdict.NeedsApproval)
        {
            return decision;
        }

        try
        {
            var session = await _sessions.FindAsync(request, cancellationToken).ConfigureAwait(false);
            if (session is null || !Guid.TryParse(session.SessionId, out var sessionId))
            {
                return decision;
            }

            var id = await _requests.FileAsync(request, sessionId, session.Role, cancellationToken).ConfigureAwait(false);
            return id is null ? decision : PushDecision.NeedsApproval($"{decision.Reason} Approval request {id:D} is waiting for the owner.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return decision;
        }
    }
}
