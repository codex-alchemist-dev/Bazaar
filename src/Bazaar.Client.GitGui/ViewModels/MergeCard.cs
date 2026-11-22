using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.ViewModels;

/// <summary>
/// What the merge button shows for one head (doc 52). It is bound to <see cref="HeadSha"/>: an approval request made from this
/// card names that sha, so a force-push after the card was built is refused by the server as <c>head_moved</c>. The card
/// only decides whether the button is on; it signs nothing and calls nothing.
/// </summary>
public sealed record MergeCard(string Provider, string Repo, long Number, string HeadSha, bool CanRequestMerge, string? BlockedReason)
{
    /// <summary>
    /// Builds the card. The button is on only when the PR is open, not a draft, and every check is <c>success</c> or
    /// <c>skipped</c> and reports on this head. A PR with no checks at all passes the check rule: nothing is failing.
    /// </summary>
    public static MergeCard Evaluate(GitPrSummaryDto pr, IReadOnlyList<GitCheckDto> checks)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(checks);

        var reason = BlockedReasonFor(pr, checks);
        return new MergeCard(pr.Provider, pr.Repo, pr.Number, pr.HeadSha, reason is null, reason);
    }

    private static string? BlockedReasonFor(GitPrSummaryDto pr, IReadOnlyList<GitCheckDto> checks)
    {
        if (!string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return $"The PR is {pr.State}.";
        }

        if (pr.Draft)
        {
            return "The PR is a draft.";
        }

        foreach (var check in checks)
        {
            if (!string.Equals(check.HeadSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return $"Check '{check.Name}' reports on an older head.";
            }

            if (!string.Equals(check.State, "success", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(check.State, "skipped", StringComparison.OrdinalIgnoreCase))
            {
                return $"Check '{check.Name}' is {check.State}.";
            }
        }

        return null;
    }
}

/// <summary>
/// Raised when the owner presses merge on an enabled <see cref="MergeCard"/>. It carries what the approval will name. Signing
/// the approval and the wire route come in a later change; nothing here calls the server.
/// </summary>
public sealed class OwnerMergeRequestedEventArgs(string provider, string repo, long number, string headSha) : EventArgs
{
    public string Provider { get; } = provider;

    public string Repo { get; } = repo;

    public long Number { get; } = number;

    public string HeadSha { get; } = headSha;
}
