using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Server.EngineDispatch;

/// <summary>
/// Exact-host matching for <c>engine.exec.http_allowlist</c> (doc 20, Exfiltration). Hosts compare whole, case-insensitively;
/// no suffix or subdomain matching. URLs with user info or a non-default port are refused.
/// </summary>
internal static partial class HostAllowlist
{
    public static bool IsAllowed(string url, IReadOnlySet<string> hosts)
    {
        if (hosts.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
        {
            return false;
        }

        return hosts.Contains(Normalize(uri.IdnHost));
    }

    public static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>The <c>url</c> argument of <c>http_get</c>, or null when absent or not a string.</summary>
    public static string? HttpGetUrl(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;

    /// <summary>
    /// Every http(s) URL literal found in the arguments (cheap detection for <c>execute_luau</c>). Concatenated or computed
    /// URLs are not detected; the exec owner approval covers those.
    /// </summary>
    public static IEnumerable<string> FindUrls(JsonElement args)
    {
        foreach (var s in Strings(args))
        {
            foreach (Match m in UrlPattern().Matches(s))
            {
                yield return m.Value;
            }
        }
    }

    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray())
                {
                    foreach (var s in Strings(i))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    foreach (var s in Strings(p.Value))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }

    [GeneratedRegex("https?://[^\\s\"'`\\\\<>)\\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
