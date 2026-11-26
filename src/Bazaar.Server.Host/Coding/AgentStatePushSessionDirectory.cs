using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Identity;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// The session fields that <see cref="IAgentStateReader"/> does not carry: the session id, the session role, the source trust
/// label and any verified approval grant. The host supplies these from its session records.
/// </summary>
/// <param name="SessionId">The coding session id; the generic policy requires a GUID in "D" format.</param>
/// <param name="Role">The session's role.</param>
/// <param name="SourceTrust">The doc 15 source trust label of the content that caused the push.</param>
/// <param name="Grant">A verified per-action approval for this push, when one exists.</param>
public sealed record PushSessionRecord(string SessionId, ToolSessionRole Role, string SourceTrust, ToolApprovalGrant? Grant = null);

/// <summary>Host lookup of the running session for a push. Returns null when no session is running, which denies the push.</summary>
public interface IPushSessionRecords
{
    /// <summary>Finds the session record for a push.</summary>
    ValueTask<PushSessionRecord?> FindAsync(PushGateRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Implements <see cref="IPushSessionDirectory"/> over <see cref="IAgentStateReader"/>: the agent's taint comes from the reader,
/// and the session fields come from <see cref="IPushSessionRecords"/>. A quarantined agent has no push session, so its pushes
/// are denied. Nothing here defaults a missing field; a missing record returns null.
/// </summary>
public sealed class AgentStatePushSessionDirectory : IPushSessionDirectory
{
    private readonly IAgentStateReader _agents;
    private readonly IPushSessionRecords _records;

    /// <summary>Creates the directory.</summary>
    public AgentStatePushSessionDirectory(IAgentStateReader agents, IPushSessionRecords records)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(records);
        _agents = agents;
        _records = records;
    }

    /// <inheritdoc />
    public async ValueTask<PushSession?> FindAsync(PushGateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_agents.IsQuarantined(request.AgentId))
        {
            return null;
        }

        var record = await _records.FindAsync(request, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        return new PushSession(
            record.SessionId,
            ToPermissionTaint(_agents.TaintFor(request.AgentId)),
            record.Role,
            record.SourceTrust,
            record.Grant);
    }

    /// <summary>Maps the Identity taint level to the permission gate's level. Any unknown level maps to hard, which fails closed.</summary>
    private static SessionTaint ToPermissionTaint(Bazaar.Server.Security.Taint.SessionTaint taint) => taint.State switch
    {
        Bazaar.Server.Security.Taint.TaintState.Clean => SessionTaint.None,
        Bazaar.Server.Security.Taint.TaintState.Soft => SessionTaint.Soft,
        _ => SessionTaint.Hard,
    };
}
