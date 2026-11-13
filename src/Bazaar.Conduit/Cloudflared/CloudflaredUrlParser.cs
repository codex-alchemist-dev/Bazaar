using System.Text.RegularExpressions;

namespace Bazaar.Conduit.Cloudflared;

/// <summary>Reads the public host of a cloudflared quick tunnel from its output.</summary>
public static partial class CloudflaredUrlParser
{
    /// <summary>Returns the host of the first <c>https://&lt;label&gt;.trycloudflare.com</c> URL in the line, or null.</summary>
    public static string? TryParseQuickTunnelHost(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var match = QuickTunnelUrl().Match(line);
        return match.Success ? match.Groups["host"].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"https://(?<host>[a-z0-9-]+\.trycloudflare\.com)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuickTunnelUrl();
}
