using Bazaar.Server.WebAdmin.Http;
using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.WebAdmin.Security;

/// <summary>
/// Reads the panel cookie (<c>__Host-bazaar-admin</c> over HTTPS, <c>bazaar-admin</c> on plain HTTP) and validates it against
/// <see cref="IAdminSessionStore"/>. No cookie, an unknown, expired or revoked session reads as nobody, so the gate answers 401.
/// </summary>
public sealed class CookieAdminSessionReader(IAdminSessionStore store, TimeProvider clock) : IAdminSessionReader
{
    public async ValueTask<AdminSession?> ReadAsync(HttpContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cookie = AdminHttp.ReadCookie(context.Request);
        if (cookie is null)
        {
            return null;
        }

        var session = await store.ValidateAsync(cookie, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return session is null ? null : new AdminSession(session.PrincipalId, session.Kind, session.DeviceId);
    }
}
