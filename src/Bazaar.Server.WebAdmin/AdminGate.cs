using System.Text;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.WebAdmin;

/// <summary>
/// Doc 51 section 2. No session gives <see cref="AdminDecision.Unauthenticated"/>. A session whose principal lacks the area's
/// capability at instance scope, or is not a human, gives <see cref="AdminDecision.Forbidden"/> and is audited
/// (<c>admin.web.denied</c>). A missing session is not audited, so unauthenticated traffic cannot flood the log.
/// </summary>
public sealed class AdminGate(IAdminSessionReader reader, IPermissionChecker checker, IAuditLog audit, TimeProvider clock) : IAdminGate
{
    private static readonly Capability _kodoViewStatus = new("kodo.view.status");
    private static readonly Capability _kodoViewAudit = new("kodo.view.audit");

    private static readonly Capability[] _anyAdmin =
    [
        Capabilities.AdminMembers, Capabilities.AdminDevices, Capabilities.AdminPrivacy,
        Capabilities.AdminBackups, Capabilities.AdminModules, Capabilities.AdminRoles, _kodoViewStatus,
    ];


    public static IReadOnlyList<Capability> CapabilitiesFor(AdminArea area) => area switch
    {
        AdminArea.Overview => _anyAdmin,
        AdminArea.Members => [Capabilities.AdminMembers],
        AdminArea.Devices => [Capabilities.AdminDevices],
        AdminArea.Privacy => [Capabilities.AdminPrivacy],
        AdminArea.Backups => [Capabilities.AdminBackups],
        AdminArea.Modules => [Capabilities.AdminModules],
        AdminArea.Kodo => [_kodoViewStatus],
        AdminArea.Audit => [_kodoViewAudit],
        AdminArea.Roles => [Capabilities.AdminRoles],
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    public async ValueTask<AdminAccess> CheckAsync(HttpContext context, AdminArea area, CancellationToken ct = default)
    {
        var session = await reader.ReadAsync(context, ct);
        if (session is null)
            return new AdminAccess(AdminDecision.Unauthenticated, null);

        var allowed = false;
        if (session.Kind == PrincipalKind.Human)
        {
            foreach (var capability in CapabilitiesFor(area))
            {
                if (await checker.IsAllowedAsync(session.PrincipalId, session.Kind, capability, Scope.Instance, ct))
                {
                    allowed = true;
                    break;
                }
            }
        }

        if (allowed)
            return new AdminAccess(AdminDecision.Allowed, session);

        await AuditDeniedAsync(session, area, context.Request.Path, ct);
        return new AdminAccess(AdminDecision.Forbidden, session);
    }

    private Task AuditDeniedAsync(AdminSession session, AdminArea area, PathString path, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("principal", session.PrincipalId.ToString("D"));
            w.WriteString("kind", session.Kind.ToString());
            w.WriteString("area", area.ToString());
            w.WriteString("route", path.Value);
            w.WriteEndObject();
        }

        return audit.AppendAsync(
            new AuditEntry(clock.GetUtcNow(), session.PrincipalId.ToString("D"), "admin.web.denied", Encoding.UTF8.GetString(stream.ToArray())), ct);
    }
}
