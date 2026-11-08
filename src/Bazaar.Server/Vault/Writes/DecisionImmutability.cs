using System.Text.RegularExpressions;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Decision immutability (doc 18, work package 9). A decision record stops being a draft once its status leaves
/// <c>proposed</c>. From then on its body is frozen: the only change allowed is <c>accepted</c> becoming
/// <c>superseded</c>, which the superseding decision records. Deleting or renaming such a file is refused too.
/// </summary>
/// <remarks>
/// Checks run on a branch's own changes (base to branch tip) before the merge, so a refused branch never touches the
/// main checkout. This is a default for the rule the design leaves open; the owner can tighten it.
/// </remarks>
internal static partial class DecisionImmutability
{
    private const string _decisionsPrefix = "decisions/";
    private const string _proposed = "proposed";
    private const string _accepted = "accepted";
    private const string _superseded = "superseded";

    /// <summary>
    /// Returns the decision paths the branch may not change, ordinal-sorted. Empty when the branch is allowed to merge.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindViolationsAsync(
        IGitProvider git,
        string repoPath,
        string baseCommit,
        string branch,
        CancellationToken ct)
    {
        var tip = await git.ResolveCommitAsync(repoPath, branch, ct);
        if (string.Equals(tip, baseCommit, StringComparison.Ordinal))
        {
            return [];
        }

        var changes = await git.DiffAsync(repoPath, baseCommit, tip, ct);
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var change in changes)
        {
            var touchesDecision = IsDecisionPath(change.Path) || (change.OldPath is not null && IsDecisionPath(change.OldPath));
            if (!touchesDecision)
            {
                continue;
            }

            // New decision files are always allowed: they start as proposed.
            if (change.Kind == DiffChangeKind.Added)
            {
                continue;
            }

            var oldPath = change.OldPath ?? change.Path;
            var oldText = await git.ReadTextAsync(repoPath, baseCommit, oldPath, ct);
            var oldStatus = StatusOf(oldText);
            if (oldStatus == _proposed)
            {
                continue;
            }

            if (change.Kind is DiffChangeKind.Deleted or DiffChangeKind.Renamed or DiffChangeKind.Copied)
            {
                violations.Add(oldPath);
                continue;
            }

            if (change.Kind == DiffChangeKind.Modified || change.Kind == DiffChangeKind.TypeChanged)
            {
                var newText = await git.ReadTextAsync(repoPath, tip, change.Path, ct);
                if (!IsAllowedEdit(oldText, newText))
                {
                    violations.Add(change.Path);
                }
            }
        }

        return [.. violations];
    }

    /// <summary>
    /// True when <paramref name="newText"/> equals <paramref name="oldText"/> except for an <c>accepted</c> to
    /// <c>superseded</c> status change.
    /// </summary>
    internal static bool IsAllowedEdit(string oldText, string newText)
    {
        var oldStatus = StatusOf(oldText);
        var newStatus = StatusOf(newText);
        if (oldStatus == newStatus)
        {
            return string.Equals(oldText, newText, StringComparison.Ordinal);
        }

        if (oldStatus == _accepted && newStatus == _superseded)
        {
            return string.Equals(
                StatusLine().Replace(oldText, "status: _", 1),
                StatusLine().Replace(newText, "status: _", 1),
                StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Reads the <c>status:</c> value from the front matter, or null when there is none.</summary>
    internal static string? StatusOf(string text)
    {
        var match = StatusLine().Match(text);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static bool IsDecisionPath(string path) => path.StartsWith(_decisionsPrefix, StringComparison.Ordinal);

    [GeneratedRegex("^status: (?<value>[a-z]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();
}
