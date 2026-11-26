using Bazaar.StatusTui;

// bazaar-status [server-url] [--watch]
// Prints the server status screen from GET /admin/api/status. Uses the panel session cookie in BAZAAR_ADMIN_COOKIE
// (value only; the name is __Host-bazaar-admin over HTTPS and bazaar-admin on loopback HTTP, doc 51 section 5).
var watch = args.Contains("--watch");
var url = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:5000";
var cookie = Environment.GetEnvironmentVariable(StatusClient.CookieEnvironmentVariable);
if (string.IsNullOrWhiteSpace(cookie) || !Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
{
    Console.Error.WriteLine("Usage: bazaar-status [server-url] [--watch]   (set BAZAAR_ADMIN_COOKIE to the panel session cookie value)");
    return 2;
}

var name = baseUri.Scheme == Uri.UriSchemeHttps ? "__Host-bazaar-admin" : "bazaar-admin";
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
do
{
    var (view, error) = await StatusClient.FetchAsync(http, baseUri, name, cookie, cts.Token);
    if (watch)
    {
        Console.Clear();
    }

    if (view is null)
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    Console.Write(StatusScreen.Render(view));
    if (watch)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch (OperationCanceledException) { break; }
    }
}
while (watch && !cts.IsCancellationRequested);

return 0;
