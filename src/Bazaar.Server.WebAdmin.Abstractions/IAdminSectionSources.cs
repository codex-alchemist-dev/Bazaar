namespace Bazaar.Server.WebAdmin;

// One read port per section, implemented by the module that owns the data. A port that is not registered makes its section
// "unavailable"; the other sections keep working (doc 51 section 5, soft edges). None of these records carries a secret.

public sealed record AdminQueueDepth(string Name, long Depth);
public sealed record AdminHealthCheck(string Name, string State, string? Detail);

/// <summary>Overall health of the server as one word (<c>healthy</c>, <c>degraded</c>, <c>failing</c> or <c>unknown</c>) plus the checks behind it.</summary>
public sealed record AdminHealthSummary(string State, IReadOnlyList<AdminHealthCheck> Checks)
{
    public static AdminHealthSummary Unknown { get; } = new("unknown", []);
}

/// <summary>The status page and the JSON the server TUI reads. <see cref="Queues"/> and <see cref="Health"/> trail with defaults so older sources keep compiling.</summary>
public sealed record AdminStatusView(
    string DisplayName,
    string Version,
    string? Codename,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    IReadOnlyList<string> ProviderLines,
    IReadOnlyList<AdminQueueDepth>? Queues = null,
    AdminHealthSummary? Health = null);

/// <summary>Contributes queue depths (jobs, mail, wake queue and so on) to the status view. Several modules may each register one.</summary>
public interface IAdminQueueDepthSource { Task<IReadOnlyList<AdminQueueDepth>> GetAsync(CancellationToken ct = default); }

/// <summary>Contributes health checks to the status view. Kodo or any other module may register one.</summary>
public interface IAdminHealthSource { Task<IReadOnlyList<AdminHealthCheck>> GetAsync(CancellationToken ct = default); }
public interface IAdminStatusSource { Task<AdminStatusView> GetAsync(CancellationToken ct = default); }

public sealed record AdminKodoCheck(string Name, string State, string? Detail);
public sealed record AdminKodoIncident(string Id, string Title, string Severity, DateTimeOffset OpenedAt, string? RunbookLink);
public sealed record AdminKodoView(int? Score, IReadOnlyList<AdminKodoCheck> Checks, IReadOnlyList<AdminKodoIncident> Incidents);
public interface IAdminKodoSource { Task<AdminKodoView> GetAsync(CancellationToken ct = default); }

public sealed record AdminUser(Guid PrincipalId, string DisplayName, string Role, bool IsAgent, bool Revoked);
public interface IAdminUserDirectory { Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default); }

public sealed record AdminDevice(Guid DeviceId, Guid PrincipalId, string Label, DateTimeOffset LastSeen, bool Revoked);
public interface IAdminDeviceDirectory { Task<IReadOnlyList<AdminDevice>> ListAsync(CancellationToken ct = default); }

public sealed record AdminAuditRow(long Seq, DateTimeOffset Ts, string Actor, string Kind, string Summary);
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditRow> Rows, long? NextBefore);
public interface IAdminAuditReader { Task<AdminAuditPage> ReadAsync(long? before, int limit, CancellationToken ct = default); }

public sealed record AdminApproval(string Id, string Action, string Requester, DateTimeOffset RequestedAt);
public interface IAdminApprovalSource { Task<IReadOnlyList<AdminApproval>> ListPendingAsync(CancellationToken ct = default); }
