using Bazaar.Server.OutcomeLedger.Abstractions;

namespace Bazaar.Server.OutcomeLedger.GitHub;

/// <summary>
/// A fact about a pull request that matters to reputation, in a form that does not depend on how it arrived. The GitHub ingress
/// (doc 21) builds one from a verified webhook or from a polled event; both paths give the same fact for the same GitHub object,
/// so the ledger sees one row.
/// </summary>
public abstract record GitHubOutcomeFact(string Repo, int PullRequest, DateTimeOffset OccurredAt, string? SourceRef = null)
{
    /// <summary>The PR reference stored on the outcome, <c>owner/repo#number</c>.</summary>
    public string PrRef => $"{Repo}#{PullRequest}";
}

/// <summary>A check run on a PR head finished. Only the first conclusive run of a PR counts (doc 11).</summary>
public sealed record CheckRunFinished(string Repo, int PullRequest, long CheckRunId, bool Succeeded, DateTimeOffset OccurredAt, string? SourceRef = null)
    : GitHubOutcomeFact(Repo, PullRequest, OccurredAt, SourceRef);

/// <summary>A merged PR was reverted. <c>RevertPullRequest</c> is the PR that reverts it. <c>MergedAt</c> bounds the revert window.</summary>
public sealed record PullRequestReverted(string Repo, int PullRequest, int RevertPullRequest, DateTimeOffset MergedAt, DateTimeOffset OccurredAt, string? SourceRef = null)
    : GitHubOutcomeFact(Repo, PullRequest, OccurredAt, SourceRef);

/// <summary>A defect was linked to a merged PR by the defect's own PR reference, within the defect window.</summary>
public sealed record PostMergeDefectLinked(string Repo, int PullRequest, int DefectPullRequest, DateTimeOffset MergedAt, DateTimeOffset OccurredAt, string? SourceRef = null)
    : GitHubOutcomeFact(Repo, PullRequest, OccurredAt, SourceRef);

/// <summary>The owner closed a card for the PR: accepted or rejected.</summary>
public sealed record OwnerDecided(string Repo, int PullRequest, bool Accepted, string DecisionId, DateTimeOffset OccurredAt, string? SourceRef = null)
    : GitHubOutcomeFact(Repo, PullRequest, OccurredAt, SourceRef);

/// <summary>Who an outcome on a PR counts for. Supplied by the PR service (doc 21), which knows the author agent and the card tag.</summary>
public interface IPrAttributionResolver
{
    /// <summary>The attributions for a PR, or empty when the PR is not an agent's (a human PR carries no agent evidence).</summary>
    Task<IReadOnlyList<OutcomeAttribution>> ResolveAsync(string repo, int pullRequest, CancellationToken cancellationToken);
}
