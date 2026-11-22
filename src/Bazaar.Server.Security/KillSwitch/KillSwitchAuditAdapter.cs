using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Writes each kill-switch engage or resume to the audit chain (doc 15, "Audit log and kill switch") as one
/// <see cref="AuditEntry"/>. The kind is <c>killswitch.engaged</c> or <c>killswitch.resumed</c>. The payload holds the
/// action, scope, agent id (null for a global stop) and reason. The actor is the engine's actor id.
/// </summary>
/// <remarks>
/// Hashing and the append lock are <see cref="IAuditLog"/>'s job. The engine calls this sink once per engage or resume, so
/// the chain gets exactly one row per call.
/// </remarks>
public sealed class KillSwitchAuditAdapter(IAuditLog log) : IKillSwitchAuditSink
{
    public const string EngagedKind = "killswitch.engaged";
    public const string ResumedKind = "killswitch.resumed";

    private readonly IAuditLog _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = new JsonObject
        {
            ["action"] = ActionName(entry.Action),
            ["scope"] = entry.Scope.ToString().ToLowerInvariant(),
            ["agent_id"] = entry.AgentId,
            ["reason"] = entry.Reason,
        };
        var audit = new AuditEntry(entry.Ts, entry.ActorId, KindFor(entry.Action), payload.ToJsonString());
        return _log.AppendAsync(audit, ct);
    }

    private static string KindFor(KillSwitchAction action) => action switch
    {
        KillSwitchAction.Engaged => EngagedKind,
        KillSwitchAction.Resumed => ResumedKind,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown kill-switch action."),
    };

    /// <summary>Wire name of an action in the payload; explicit so it does not depend on enum ToString.</summary>
    private static string ActionName(KillSwitchAction action) => action switch
    {
        KillSwitchAction.Engaged => "engaged",
        KillSwitchAction.Resumed => "resumed",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown kill-switch action."),
    };
}
