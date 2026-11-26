using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>Writes the one <c>setup.firstrun.seeded</c> audit row: ids and a flag, never a name, a purpose or a secret.</summary>
public sealed class AuditLogFirstRunAudit(IAuditLog audit, TimeProvider? time = null) : IFirstRunAudit
{
    /// <summary>The audit kind.</summary>
    public const string Kind = "setup.firstrun.seeded";

    /// <inheritdoc />
    public Task WriteSeededAsync(Guid? gcId, Guid? agentId, bool constitutionAccepted, string operatorIdentity, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["gcId"] = gcId?.ToString(),
            ["agentId"] = agentId?.ToString(),
            ["constitutionAccepted"] = constitutionAccepted,
        };
        return audit.AppendAsync(new AuditEntry((time ?? TimeProvider.System).GetUtcNow(), operatorIdentity, Kind, payload.ToJsonString()), cancellationToken);
    }
}
