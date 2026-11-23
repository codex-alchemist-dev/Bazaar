namespace Bazaar.Server.CodingGit;

/// <summary>
/// Refuses pushes of the default branch and of anything that is not an <c>agent/*</c> branch (doc 17: agents never
/// push to the default branch). Runs before any port is called.
/// </summary>
public static class DefaultBranchGuard
{
    private const string _headsPrefix = "refs/heads/";

    /// <summary>
    /// True when <paramref name="branch"/> names the default branch, ignoring case, whitespace and a
    /// <c>refs/heads/</c> prefix. A blank branch or a blank default branch matches (fail closed).
    /// </summary>
    public static bool IsDefaultBranch(string? branch, string? defaultBranch)
    {
        if (string.IsNullOrWhiteSpace(defaultBranch) || string.IsNullOrWhiteSpace(branch))
        {
            return true;
        }

        return string.Equals(Normalize(branch), Normalize(defaultBranch), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the branch as a <see cref="BranchName"/>, or throws when the push must be refused.</summary>
    /// <exception cref="CodingGitException">
    /// <see cref="CodingGitError.DefaultBranchRefused"/> for the default branch, <see cref="CodingGitError.BranchRefused"/>
    /// for any other branch that is not <c>agent/*</c>.
    /// </exception>
    public static BranchName RequireAgentBranch(string? branch, string? defaultBranch)
    {
        if (IsDefaultBranch(branch, defaultBranch))
        {
            throw new CodingGitException(CodingGitError.DefaultBranchRefused, "Agents never push the default branch.");
        }

        if (!BranchName.TryParse(branch, out var name))
        {
            throw new CodingGitException(CodingGitError.BranchRefused, "Only agent/<task> branches can be pushed.");
        }

        return name;
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(_headsPrefix, StringComparison.Ordinal) ? trimmed[_headsPrefix.Length..] : trimmed;
    }
}
