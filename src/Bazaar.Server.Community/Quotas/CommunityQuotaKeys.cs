namespace Bazaar.Server.Community.Quotas;

/// <summary>
/// Builds the opaque <c>rate_bucket</c> keys for community quotas (doc 36, "Rate limits, audit and takedown").
/// Keys are <c>community:{agentId}:{platform}:{scope}</c>, or <c>community:{agentId}:{platform}:{repoRef}:{scope}</c>
/// for per-repository scopes. Parts may not contain ':' so keys cannot collide across agents or platforms.
/// </summary>
public static class CommunityQuotaKeys
{
    /// <summary>New public items per platform per day (shared with doc 15).</summary>
    public const string PublicItemsScope = "public_items";

    /// <summary>New issue or PR per repository per day (shared with doc 21).</summary>
    public const string RepoIssuePrScope = "repo_issue_pr";

    /// <summary>New repositories per agent per week.</summary>
    public const string NewReposScope = "new_repos";

    private const string _prefix = "community";

    /// <summary>Builds a bucket key for a per-agent, per-platform scope.</summary>
    /// <exception cref="ArgumentException">A part is empty, contains ':', is not lower-case (platform), or the scope is unknown.</exception>
    public static string Key(string agentId, string platform, string scope)
    {
        ValidateIdentity(agentId, platform);
        ValidateScope(scope);
        return $"{_prefix}:{agentId}:{platform}:{scope}";
    }

    /// <summary>Builds a bucket key for a per-repository scope.</summary>
    /// <exception cref="ArgumentException">A part is empty, contains ':', is not lower-case (platform), or the scope is unknown.</exception>
    public static string RepoKey(string agentId, string platform, string repoRef, string scope)
    {
        ValidateIdentity(agentId, platform);
        ValidatePart(repoRef, nameof(repoRef));
        ValidateScope(scope);
        return $"{_prefix}:{agentId}:{platform}:{repoRef}:{scope}";
    }

    /// <summary>Checks the agent and platform parts shared by every community key and freeze record.</summary>
    /// <exception cref="ArgumentException">The agent or platform is empty, contains ':', or the platform is not lower-case.</exception>
    public static void ValidateIdentity(string agentId, string platform)
    {
        ValidatePart(agentId, nameof(agentId));
        ValidatePart(platform, nameof(platform));
        if (!string.Equals(platform, platform.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Platform names must be lower-case.", nameof(platform));
        }
    }

    private static void ValidateScope(string scope)
    {
        if (scope is not (PublicItemsScope or RepoIssuePrScope or NewReposScope))
        {
            throw new ArgumentException($"Unknown community quota scope '{scope}'.", nameof(scope));
        }
    }

    private static void ValidatePart(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Key parts must not be empty.", name);
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Key parts must not contain ':'.", name);
        }
    }
}
