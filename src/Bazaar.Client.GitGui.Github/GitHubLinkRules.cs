namespace Bazaar.Client.GitGui.Github;

/// <summary>
/// Strict input rules for GitHub links (doc 52, Client). A repo name, a commit sha and a PR number are validated before any
/// URL is built, so no caller-supplied text reaches a link unchecked.
/// </summary>
internal static class GitHubLinkRules
{
    /// <summary>The PR page URL, or null when the repo name or number is invalid.</summary>
    internal static Uri? PrPage(string repoFullName, long number)
    {
        if (number <= 0 || !IsRepoFullName(repoFullName))
        {
            return null;
        }

        return Build($"https://github.com/{repoFullName}/pull/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    /// <summary>The commit page URL, or null when the repo name or sha is invalid.</summary>
    internal static Uri? CommitPage(string repoFullName, string sha)
    {
        if (!IsRepoFullName(repoFullName) || !IsSha(sha))
        {
            return null;
        }

        return Build($"https://github.com/{repoFullName}/commit/{sha.ToLowerInvariant()}");
    }

    /// <summary>GitHub repo names are exactly <c>owner/name</c>, each segment on the allowlist.</summary>
    internal static bool IsRepoFullName(string? repoFullName)
    {
        if (string.IsNullOrEmpty(repoFullName))
        {
            return false;
        }

        var parts = repoFullName.Split('/');
        return parts.Length == 2 && IsSegment(parts[0]) && IsSegment(parts[1]);
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
