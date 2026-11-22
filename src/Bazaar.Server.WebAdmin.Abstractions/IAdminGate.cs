using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.WebAdmin;

public enum AdminDecision
{
    Allowed,

    /// <summary>No session: answer 401.</summary>
    Unauthenticated,

    /// <summary>A session without the capability, or a non-human principal: answer 403.</summary>
    Forbidden,
}

public readonly record struct AdminAccess(AdminDecision Decision, AdminSession? Session);

/// <summary>The panel's authorization check (doc 51 section 2), over the roles model of doc 48 section 4.</summary>
public interface IAdminGate
{
    ValueTask<AdminAccess> CheckAsync(HttpContext context, AdminArea area, CancellationToken ct = default);
}
