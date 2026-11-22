using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard;

/// <summary>Which door a sign-in came through (doc 58 section 5).</summary>
public enum OpsSignInMethod
{
    /// <summary>The owner on the box, through the local gate.</summary>
    OwnerLocal,

    /// <summary>A short-lived single-use code issued from a signed-in client.</summary>
    AdminCode,
}

/// <summary>A signed-in Ops session. The cookie value is kept in memory only and is never drawn or logged.</summary>
public sealed class OpsSession(AdminIdentity identity, string cookieValue, PanelSession session, OpsSignInMethod method)
{
    public AdminIdentity Identity => identity;

    public OpsSignInMethod Method => method;

    public PanelSession Panel => session;

    internal string CookieValue => cookieValue;
}

/// <summary>
/// Ops sign-in (doc 58 section 5): the owner through the local gate, other admins by redeeming a single-use code for a session in the
/// doc 51 session store, under the roles check. Every attempt is audited with the web admin's audit kinds. The code is never written to
/// the audit fields. A session is checked against the store on each <see cref="IsActiveAsync"/>, so revocation ends it at once.
/// </summary>
public sealed class OpsSignIn(
    IOwnerLocalGate? ownerGate,
    IAdminHandoffRedeemer? redeemer,
    IAdminSessionStore sessions,
    IOpsAuthorizer authorizer,
    IAdminAudit audit,
    TimeProvider clock)
{
    private const string _anonymous = "anonymous";

    /// <summary>The owner's door. Null when the gate is closed or not wired; the denial is audited.</summary>
    public async Task<OpsSession?> SignInOwnerAsync(CancellationToken ct = default)
    {
        var identity = ownerGate is null ? null : await ownerGate.EnterAsync(ct).ConfigureAwait(false);
        if (identity is null)
        {
            await DenyAsync(_anonymous, OpsSignInMethod.OwnerLocal, ct).ConfigureAwait(false);
            return null;
        }

        return await OpenAsync(identity, OpsSignInMethod.OwnerLocal, ct).ConfigureAwait(false);
    }

    /// <summary>The admin door: redeems a single-use code. Unknown, used or expired codes, and identities without Ops rights, are refused and audited.</summary>
    public async Task<OpsSession?> SignInWithCodeAsync(string code, CancellationToken ct = default)
    {
        var identity = redeemer is null || string.IsNullOrWhiteSpace(code)
            ? null
            : await redeemer.RedeemAsync(code.Trim(), ct).ConfigureAwait(false);
        if (identity is null)
        {
            await DenyAsync(_anonymous, OpsSignInMethod.AdminCode, ct).ConfigureAwait(false);
            return null;
        }

        if (!await authorizer.MayUseOpsAsync(identity, ct).ConfigureAwait(false))
        {
            await DenyAsync(identity.PrincipalId.ToString("D"), OpsSignInMethod.AdminCode, ct).ConfigureAwait(false);
            return null;
        }

        return await OpenAsync(identity, OpsSignInMethod.AdminCode, ct).ConfigureAwait(false);
    }

    /// <summary>True while the store still honours the session. A sliding or absolute expiry, or a revoked device, makes it false.</summary>
    public async Task<bool> IsActiveAsync(OpsSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await sessions.ValidateAsync(session.CookieValue, clock.GetUtcNow(), ct).ConfigureAwait(false) is not null;
    }

    public async Task SignOutAsync(OpsSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await sessions.RevokeAsync(session.Panel.Id, ct).ConfigureAwait(false);
        await audit.RecordAsync(
            AdminAuditKinds.SignOut,
            session.Identity.PrincipalId.ToString("D"),
            new Dictionary<string, string> { ["surface"] = "tui" },
            ct).ConfigureAwait(false);
    }

    private async Task<OpsSession> OpenAsync(AdminIdentity identity, OpsSignInMethod method, CancellationToken ct)
    {
        // The audit entry comes first: a sign-in whose entry cannot be appended is refused (doc 51 section 5), so a throw here opens nothing.
        await audit.RecordAsync(
            AdminAuditKinds.SignIn,
            identity.PrincipalId.ToString("D"),
            new Dictionary<string, string> { ["surface"] = "tui", ["method"] = MethodWord(method) },
            ct).ConfigureAwait(false);
        var created = await sessions.CreateAsync(identity, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return new OpsSession(identity, created.CookieValue, created.Session, method);
    }

    private Task DenyAsync(string actor, OpsSignInMethod method, CancellationToken ct) =>
        audit.RecordAsync(
            AdminAuditKinds.Denied,
            actor,
            new Dictionary<string, string> { ["surface"] = "tui", ["method"] = MethodWord(method) },
            ct);

    private static string MethodWord(OpsSignInMethod method) => method == OpsSignInMethod.OwnerLocal ? "owner-local" : "admin-code";
}
