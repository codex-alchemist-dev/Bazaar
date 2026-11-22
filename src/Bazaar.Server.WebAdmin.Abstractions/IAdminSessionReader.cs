using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.WebAdmin;

/// <summary>
/// Turns a request into the signed-in session, or nothing. The passkey sign-in module (doc 32 section 4) supplies the real
/// reader and is the only place that checks the device is not revoked, so a revoked device reads as no session at once.
/// </summary>
public interface IAdminSessionReader
{
    ValueTask<AdminSession?> ReadAsync(HttpContext context, CancellationToken ct = default);
}
