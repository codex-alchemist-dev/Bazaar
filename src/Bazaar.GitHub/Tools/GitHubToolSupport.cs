using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;

namespace Bazaar.GitHub.Tools;

/// <summary>Shared argument validation and result shaping for the agent GitHub tools (doc 21, Agent tools [PROPOSED]).</summary>
internal static class GitHubToolSupport
{
    public const int MaxBodyChars = 20_000;
    public const int MaxPatchCharsPerFile = 8_000;
    public const int MaxFiles = 100;

    public static JsonObject Refuse(string status, string message) => new() { ["ok"] = false, ["status"] = status, ["message"] = message };

    /// <summary>Validates owner, repo and number. Names allow GitHub's alphabet only, so no path segment can be injected.</summary>
    public static bool TryReadPr(JsonElement args, out string owner, out string repo, out int number, out JsonObject? refusal)
    {
        owner = repo = string.Empty;
        number = 0;
        refusal = null;
        if (args.ValueKind != JsonValueKind.Object)
        {
            refusal = Refuse("invalid_arguments", "Arguments must be a JSON object.");
            return false;
        }

        if (!TryName(args, "owner", 39, allowDotUnderscore: false, out owner))
        {
            refusal = Refuse("invalid_arguments", "owner must be a GitHub account name (letters, digits, hyphen; at most 39 characters).");
            return false;
        }

        if (!TryName(args, "repo", 100, allowDotUnderscore: true, out repo) || repo is "." or "..")
        {
            refusal = Refuse("invalid_arguments", "repo must be a GitHub repository name (letters, digits, hyphen, underscore, dot; at most 100 characters).");
            return false;
        }

        if (!args.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out number) || number < 1)
        {
            refusal = Refuse("invalid_arguments", "number must be a positive integer.");
            return false;
        }

        return true;
    }

    public static bool TryHeadSha(JsonElement args, out string sha)
    {
        sha = string.Empty;
        if (!args.TryGetProperty("headSha", out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = p.GetString();
        if (text is not { Length: 40 } || !text.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        sha = text;
        return true;
    }

    private static bool TryName(JsonElement args, string name, int max, bool allowDotUnderscore, out string value)
    {
        value = string.Empty;
        if (!args.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = p.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > max)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || (allowDotUnderscore && (c == '.' || c == '_'))))
            {
                return false;
            }
        }

        value = text;
        return true;
    }

    /// <summary>
    /// Resolves the calling agent's installation and checks the repository is visible to it (GitHub answers 404 for a repository the
    /// App is not installed on). Returns the installation, or a refusal. Never reveals tokens or GitHub's message text.
    /// </summary>
    public static async Task<(long Installation, JsonObject? Refusal)> ResolveAsync(
        IGitHubAgentInstallations installations, IGitHubAdapter adapter, IGitHubTokenProvider tokens,
        Guid agentId, string owner, string repo, CancellationToken cancellationToken)
    {
        if (tokens is UnconfiguredGitHubTokenProvider)
        {
            return (0, Refuse("not_configured", "GitHub is not configured on this server (no token source). Nothing was sent."));
        }

        var installation = await installations.GetInstallationIdAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (installation is not { } id)
        {
            return (0, Refuse("no_installation", "This agent has no active GitHub App installation."));
        }

        try
        {
            await adapter.GetRepositoryNodeIdAsync(id, owner, repo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GitHubApiException or GitHubTokenException)
        {
            return (0, Refuse("repo_not_accessible", "The agent's GitHub App is not installed on that repository, or it cannot be read."));
        }

        return (id, null);
    }

    /// <summary>Maps adapter failures to a fixed, secret-free result. Other exceptions are not caught.</summary>
    public static bool IsHandled(Exception e) => e is GitHubApiException or GitHubTokenException;

    public static JsonObject Failure(Exception e) => e switch
    {
        GitHubRateLimitedException => Refuse("rate_limited", "GitHub rate limit reached. Try again later."),
        GitHubTokenException => Refuse("not_configured", "A GitHub token could not be obtained. Nothing was sent."),
        GitHubApiException api => Refuse("github_error", $"GitHub refused the request (HTTP {(int?)api.StatusCode ?? 0})."),
        _ => Refuse("error", "The GitHub call failed."),
    };
}
