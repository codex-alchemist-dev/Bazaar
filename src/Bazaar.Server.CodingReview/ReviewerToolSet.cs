namespace Bazaar.Server.CodingReview;

/// <summary>The result of checking one tool name against the reviewer allowlist.</summary>
public sealed record ReviewerToolAuthorization(bool Allowed, string Reason);

/// <summary>
/// The reviewer's fixed, read-only tool set (doc 17, Review flow and subagents): read, list, search, and git diff, show and log.
/// There is no shell or exec, no edit, no push and no network. The allowlist is a closed set of exact, case-sensitive names, and
/// any other name is refused, including empty and null names. The server enforces this in tool dispatch.
/// </summary>
public static class ReviewerToolSet
{
    /// <summary>The only tool names a reviewer session may call.</summary>
    public static IReadOnlyList<string> AllowedToolNames { get; } = ["read", "list", "search", "git_diff", "git_show", "git_log"];

    private static readonly HashSet<string> _allowed = new(AllowedToolNames, StringComparer.Ordinal);

    /// <summary>Decides whether a reviewer session may call <paramref name="toolName"/>. Anything outside the closed set is refused.</summary>
    public static ReviewerToolAuthorization Authorize(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return new ReviewerToolAuthorization(false, "Empty tool name is refused.");
        }

        return _allowed.Contains(toolName)
            ? new ReviewerToolAuthorization(true, "Read-only reviewer tool.")
            : new ReviewerToolAuthorization(false, $"Tool '{toolName}' is not on the reviewer allowlist; reviewers are read-only.");
    }
}
