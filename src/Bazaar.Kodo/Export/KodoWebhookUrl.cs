using System.Globalization;

namespace Bazaar.Kodo.Export;

/// <summary>
/// Webhook URLs are secrets (doc 46 section 15.3): the path and query often carry the token. This type is the only way a webhook URL
/// reaches a log, a journal line or a view. It keeps the scheme, host and port and drops everything else.
/// </summary>
public static class KodoWebhookUrl
{
    /// <summary>What stands in for an input that is not an absolute URL. The input itself is never echoed.</summary>
    public const string UnparsedText = "[unparsed webhook url]";

    /// <summary>The path that replaces the real path and query.</summary>
    public const string RedactedPath = "/[redacted]";

    /// <summary>
    /// The loggable form of a URL: <c>scheme://host[:port]/[redacted]</c>. User info, path, query and fragment never appear.
    /// </summary>
    /// <param name="url">The URL as configured. Any text is accepted; a non-URL becomes <see cref="UnparsedText"/>.</param>
    public static string Redact(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            return UnparsedText;
        }

        string host = uri.Host.Contains(':', StringComparison.Ordinal) && !uri.Host.StartsWith('[')
            ? $"[{uri.Host}]"
            : uri.Host;
        string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{host}{port}{RedactedPath}";
    }
}
