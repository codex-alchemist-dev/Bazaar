using System.Globalization;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>The one <c>setup.firstrun.seeded</c> audit row (doc 45 section 1.15): ids and flags only, never a name or a secret.</summary>
public sealed class AuditLogFirstRunAudit(IAuditLog log, TimeProvider? time = null) : IFirstRunAudit
{
    /// <summary>The audit kind.</summary>
    public const string Kind = "setup.firstrun.seeded";

    /// <inheritdoc />
    public Task WriteSeededAsync(Guid? gcId, Guid? agentId, bool constitutionAccepted, string operatorIdentity, CancellationToken cancellationToken = default)
    {
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"{{\"gcId\":{Quote(gcId)},\"agentId\":{Quote(agentId)},\"constitutionAccepted\":{(constitutionAccepted ? "true" : "false")}}}");
        return log.AppendAsync(new AuditEntry((time ?? TimeProvider.System).GetUtcNow(), operatorIdentity, Kind, payload), cancellationToken);
    }

    private static string Quote(Guid? id) => id is { } g ? $"\"{g:D}\"" : "null";
}
