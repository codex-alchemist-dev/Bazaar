namespace Bazaar.Client.GitGui.Gitlab;

/// <summary>
/// Strict input and origin rules for GitLab links (doc 52, Client). Project paths may hold subgroups; every segment is
/// checked against the allowlist before a URL is built.
/// </summary>
internal static class GitLabLinkRules
{
    /// <summary>
    /// Validates a self-managed base URL: an absolute https origin with no userinfo, query or fragment. Returns it without
    /// a trailing slash. Throws <see cref="ArgumentException"/> when it is not acceptable.
    /// </summary>
    internal static string NormaliseBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        // Whitespace, control characters and backslashes have no place in an origin; Uri would otherwise fix them up silently.
        foreach (var c in baseUrl)
        {
            if (c <= ' ' || c == '\\' || c == '\u007f')
            {
                throw new ArgumentException("The GitLab base URL contains a disallowed character.", nameof(baseUrl));
            }
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
        {
            throw new ArgumentException("The GitLab base URL must be an absolute https URL.", nameof(baseUrl));
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("The GitLab base URL must not contain userinfo, a query or a fragment.", nameof(baseUrl));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>The merge request page URL, or null when the project path or number is invalid.</summary>
    internal static Uri? MergeRequestPage(string baseUrl, string fullPath, long number)
    {
        if (number <= 0 || !IsFullPath(fullPath))
        {
            return null;
        }

        return Build($"{baseUrl}/{fullPath}/-/merge_requests/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    /// <summary>The commit page URL, or null when the project path or sha is invalid.</summary>
    internal static Uri? CommitPage(string baseUrl, string fullPath, string sha)
    {
        if (!IsFullPath(fullPath) || !IsSha(sha))
        {
            return null;
        }

        return Build($"{baseUrl}/{fullPath}/-/commit/{sha.ToLowerInvariant()}");
    }

    /// <summary>A GitLab project path: two or more segments on the allowlist. A <c>-</c> segment is refused, since it is the URL separator.</summary>
    internal static bool IsFullPath(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        var parts = fullPath.Split('/');
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part == "-" || !IsSegment(part))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A segment of letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, never <c>.</c> or any run of <c>..</c>.</summary>
    internal static bool IsSegment(string segment)
    {
        if (segment.Length == 0 || segment == "." || segment.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in segment)
        {
            var allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A hex commit sha of 7 to 64 characters.</summary>
    internal static bool IsSha(string? sha)
    {
        if (sha is null || sha.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var c in sha)
        {
            var hex = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    private static Uri? Build(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
}
