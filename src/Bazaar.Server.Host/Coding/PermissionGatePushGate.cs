using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.Host.Coding;

/// <summary>What the host knows about the session behind a push. Supplied by the host from its session records.</summary>
/// <param name="SessionId">The coding session id; the generic policy requires a GUID in "D" format.</param>
/// <param name="Taint">The session's taint.</param>
/// <param name="Role">The session's role.</param>
/// <param name="SourceTrust">The doc 15 source trust label of the content that caused the push.</param>
/// <param name="Grant">A verified per-action approval for this push, when one exists.</param>
public sealed record PushSession(
    string SessionId,
    SessionTaint Taint,
    ToolSessionRole Role,
    string SourceTrust,
    ToolApprovalGrant? Grant = null);

/// <summary>
/// Host lookup of the session behind a push. The harness's push request names the agent, group chat and task but not the
/// session, and the gate needs the session's taint, role and any approval grant. Returns null when no such session is
/// running, which denies the push.
/// </summary>
public interface IPushSessionDirectory
{
    /// <summary>Finds the session for a push.</summary>
    ValueTask<PushSession?> FindAsync(PushGateRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Binds the git module's <see cref="IPushGate"/> to the permission gate (<see cref="IToolPermissionGate"/>): a push is judged
/// as a <see cref="ToolCapabilityClass.Push"/> call to <see cref="ToolName"/> targeting the branch, so the same taint, approval,
/// freeze and trace rules apply. A missing session or any gate failure denies.
/// </summary>
public sealed class PermissionGatePushGate : IPushGate
{
    /// <summary>The tool name a push is judged under.</summary>
    public const string ToolName = "git.push";

    private readonly IToolPermissionGate _gate;
    private readonly IPushSessionDirectory _sessions;

    /// <summary>Creates the adapter.</summary>
    public PermissionGatePushGate(IToolPermissionGate gate, IPushSessionDirectory sessions)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(sessions);
        _gate = gate;
        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask<PushDecision> EvaluateAsync(PushGateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.FindAsync(request, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return PushDecision.Deny("No running session for this push.");
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            repo = request.Repo.Id,
            branch = request.Branch.Value,
            gc = request.GcId,
            task = request.TaskId,
        });
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(args)));

        var context = new ToolCallContext(
            request.AgentId,
            session.SessionId,
            ToolName,
            ToolCapabilityClass.Push,
            hash,
            session.SourceTrust,
            session.Taint,
            TargetBranch: request.Branch.Value,
            Args: args,
            Role: session.Role,
            Grant: session.Grant);

        var decision = await _gate.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
        return decision.Outcome switch
        {
            PermissionOutcome.Allow => PushDecision.Allow(decision.Reason.Length == 0 ? "allowed" : decision.Reason),
            PermissionOutcome.RequireApproval => PushDecision.NeedsApproval(decision.Reason),
            _ => PushDecision.Deny(decision.Reason),
        };
    }
}
