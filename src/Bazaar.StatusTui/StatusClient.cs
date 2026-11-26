using Bazaar.Server.WebAdmin;

namespace Bazaar.StatusTui;

/// <summary>
/// Reads <c>GET /admin/api/status</c> with an existing panel session. The TUI cannot run a passkey ceremony, so the operator
/// signs in through the panel and passes the session cookie value in <c>BAZAAR_ADMIN_COOKIE</c>; the cookie is never printed.
/// </summary>
internal static class StatusClient
{
    public const string CookieEnvironmentVariable = "BAZAAR_ADMIN_COOKIE";

    public static async Task<(AdminStatusView? View, string? Error)> FetchAsync(HttpClient http, Uri baseUri, string cookieName, string cookieValue, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/admin/api/status"));
        req.Headers.Add("Cookie", $"{cookieName}={cookieValue}");
        req.Headers.Add("Accept", "application/json");
        try
        {
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return (null, res.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "Not signed in. Sign in on the admin panel and pass the new session cookie.",
                    System.Net.HttpStatusCode.Forbidden => "This account may not read the status page.",
                    _ => $"The server answered HTTP {(int)res.StatusCode}.",
                });
            }

            var view = StatusJson.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return view is null ? (null, "The server has no status to show.") : (view, null);
        }
        catch (HttpRequestException e)
        {
            return (null, "Could not reach the server: " + e.Message);
        }
    }
}
