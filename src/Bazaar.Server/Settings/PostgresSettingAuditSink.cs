using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Settings;

/// <summary>
/// Writes each settings write attempt to the audit log (doc 03 registry, doc 15 <c>audit_log</c>) as one
/// <see cref="AuditEntry"/>. An accepted change is <c>setting.changed</c>, a held one <c>setting.held</c> and a refused one
/// <c>setting.rejected</c>. The payload holds the key, scope, scope id, actor, outcome, old and new values and the reason.
/// Hashing and the append lock are <see cref="PostgresAuditLog"/>'s job.
/// </summary>
public sealed class PostgresSettingAuditSink(IAuditLog log) : ISettingAuditSink
{
    public const string ChangedKind = "setting.changed";
    public const string HeldKind = "setting.held";
    public const string RejectedKind = "setting.rejected";

    public Task AppendAsync(SettingChangeAudit change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var entry = new AuditEntry(DateTimeOffset.UtcNow, change.ActorId, KindFor(change.Outcome), PayloadFor(change));
        return log.AppendAsync(entry, cancellationToken);
    }

    private static string KindFor(SettingWriteOutcome outcome) => outcome switch
    {
        SettingWriteOutcome.Accepted => ChangedKind,
        SettingWriteOutcome.PendingApproval => HeldKind,
        SettingWriteOutcome.Rejected => RejectedKind,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown settings write outcome."),
    };

    private static string PayloadFor(SettingChangeAudit change)
    {
        var payload = new JsonObject
        {
            ["key"] = change.Key,
            ["scope"] = change.Scope.ToString().ToLowerInvariant(),
            ["scope_id"] = change.ScopeId,
            ["actor"] = change.ActorId,
            ["outcome"] = change.Outcome.ToString().ToLowerInvariant(),
            ["old"] = AsJson(change.OldValueJson),
            ["new"] = AsJson(change.NewValueJson),
            ["reason"] = change.Reason,
        };
        return payload.ToJsonString();
    }

    /// <summary>The value as JSON: the parsed node when the text is JSON, else the raw text as a JSON string.</summary>
    private static JsonNode? AsJson(string? text)
    {
        if (text is null)
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }
}
