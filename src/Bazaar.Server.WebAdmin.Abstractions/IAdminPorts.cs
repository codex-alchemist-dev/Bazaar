using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.WebAdmin;

/// <summary>An authenticated admin identity produced by redeeming a passkey handoff code (doc 32 section 4, audience admin).</summary>
public sealed record AdminIdentity(Guid PrincipalId, PrincipalKind Kind, Guid DeviceId);

/// <summary>A live panel cookie session. <see cref="Id"/> is the hash of the cookie value, never the value itself. Capabilities are not stored: <see cref="IAdminGate"/> decides per request.</summary>
public sealed record PanelSession(
    string Id,
    Guid PrincipalId,
    PrincipalKind Kind,
    Guid DeviceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastSeen,
    DateTimeOffset AbsoluteExpiry);

/// <summary>Result of creating a session: the opaque cookie value (shown once) and the stored session.</summary>
public sealed record CreatedPanelSession(string CookieValue, PanelSession Session);

/// <summary>Session lifetime settings (<c>webadmin.session.*</c>). Floors: sliding at most 15 min, absolute at most 8 h.</summary>
public sealed record AdminSessionOptions(TimeSpan Sliding, TimeSpan Absolute)
{
    public static readonly TimeSpan MaxSliding = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxAbsolute = TimeSpan.FromHours(8);

    public static AdminSessionOptions Default { get; } = new(MaxSliding, MaxAbsolute);

    /// <summary>Settings may tighten and never loosen past the floors.</summary>
    public AdminSessionOptions Clamped() => new(
        Sliding <= TimeSpan.Zero || Sliding > MaxSliding ? MaxSliding : Sliding,
        Absolute <= TimeSpan.Zero || Absolute > MaxAbsolute ? MaxAbsolute : Absolute);
}

/// <summary>Redeems the 60 s single-use handoff code issued by <c>/signin</c> with <c>audience=admin</c>. Null when the code is unknown, used or expired.</summary>
public interface IAdminHandoffRedeemer
{
    Task<AdminIdentity?> RedeemAsync(string code, CancellationToken ct = default);
}

/// <summary>Answers whether a device or principal is still valid. Checked on every request, so revocation ends a session at once.</summary>
public interface IAdminRevocationCheck
{
    Task<bool> IsRevokedAsync(Guid principalId, Guid deviceId, CancellationToken ct = default);
}

/// <summary>Server-side session store. Only the hash of the cookie value is kept.</summary>
public interface IAdminSessionStore
{
    Task<CreatedPanelSession> CreateAsync(AdminIdentity identity, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Returns the session and slides it, or null when unknown, expired (sliding or absolute) or revoked.</summary>
    Task<PanelSession?> ValidateAsync(string cookieValue, DateTimeOffset now, CancellationToken ct = default);

    Task RevokeAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>Records admin actions in the audit log (doc 51 section 5). A write whose entry cannot be appended must be refused.</summary>
public interface IAdminAudit
{
    /// <param name="actor">The actor: a principal id in <c>D</c> form, or <c>anonymous</c> before sign-in.</param>
    Task RecordAsync(string kind, string actor, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default);
}

/// <summary>Audit kinds (doc 51 section 5).</summary>
public static class AdminAuditKinds
{
    public const string SignIn = "admin.web.signin";
    public const string SignOut = "admin.web.signout";
    public const string Denied = "admin.web.denied";
    public const string Read = "admin.web.read";
    public const string SelectionCommit = "admin.web.selection.commit";
    public const string RebuildStart = "admin.web.rebuild.start";
}
