namespace Bazaar.Server.Git.Merge;

/// <summary>Merge gate settings (doc 52, Settings). Bound from <c>Bazaar:Git</c> by <c>AddBazaarGit</c>.</summary>
public sealed class GitMergeOptions
{
    /// <summary>Setting <c>git.merge_requires_green_checks</c>. When true (the default), every check on the head must be Success or Skipped; Pending fails.</summary>
    public bool RequireGreenChecks { get; init; } = true;
}
