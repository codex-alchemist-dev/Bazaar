using System.Text.RegularExpressions;

namespace Bazaar.Server.Setup;

/// <summary>Shape checks for the parts of a git template entry that reach a process or a folder: the URL, the commit and the subfolder.</summary>
public static partial class GitTemplateRules
{
    [GeneratedRegex("^([0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashRegex();

    /// <summary>True for a full commit id: 40 or 64 lower-case hex characters.</summary>
    public static bool IsCommitId(string text) => CommitRegex().IsMatch(text);

    /// <summary>True for a template hash: 64 lower-case hex characters.</summary>
    public static bool IsTemplateHash(string text) => HashRegex().IsMatch(text);

    /// <summary>
    /// Checks a repository URL. Returns null when it is acceptable, or a fixed message that does not repeat the URL. The URL must be
    /// absolute with an allowed scheme and no whitespace or control characters, no query, no fragment, and no credentials (an ssh user
    /// name without a password is fine). A leading dash is refused, so it can never be read as an option.
    /// </summary>
    public static string? CheckUrl(string url, IReadOnlyList<string> allowedSchemes)
    {
        ArgumentNullException.ThrowIfNull(allowedSchemes);
        if (string.IsNullOrEmpty(url) || url.StartsWith('-') || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "must be one absolute git URL with no spaces.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.OriginalString.Contains('\\', StringComparison.Ordinal))
        {
            return "must be an absolute git URL such as https://host/path.git.";
        }

        if (!allowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return "uses a URL scheme this install does not allow for template repositories.";
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || url.Contains('?', StringComparison.Ordinal) || url.Contains('#', StringComparison.Ordinal))
        {
            return "must not carry a query or a fragment.";
        }

        var hasCredentials = uri.UserInfo.Length > 0 && (uri.UserInfo.Contains(':', StringComparison.Ordinal) || !string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase));
        if (hasCredentials)
        {
            return "must not carry credentials. Use the server's own git or ssh setup for private repositories.";
        }

        return string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase) || uri.Host.Length > 0
            ? null
            : "must name a host.";
    }

    /// <summary>
    /// Checks the subfolder of a template inside its repository. Empty is the repository root. Otherwise it must be relative with
    /// forward slashes and plain names: no dot segments, no <c>.git</c> segment, no drive letter, no colon.
    /// </summary>
    public static string? CheckSubfolder(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)
            || path.Any(char.IsControl))
        {
            return "must be a relative folder with forward slashes, such as templates/starter.";
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.StartsWith(".git", StringComparison.OrdinalIgnoreCase) || segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return "must be plain folder names: no empty, dot or .git segments.";
            }
        }

        return null;
    }
}
