namespace Bazaar.Server.GitHub.Events;

/// <summary>What to do with an incoming event: keep it in the log, and whether to apply it to PR state.</summary>
public readonly record struct GitHubEventDecision(bool Store, bool Apply);

/// <summary>
/// Out-of-order rule (doc 21, Behaviour): an older event is stored, never applied over newer state.
/// Pure: the caller passes the last applied state for the same PR.
/// </summary>
public static class GitHubEventOrdering
{
    /// <summary>Every event is stored. Whether it is applied depends on <see cref="ShouldApply"/>.</summary>
    public static GitHubEventDecision Decide(GitHubEvent incoming, DateTimeOffset? lastAppliedUpdatedAt, string? lastAppliedHeadSha) =>
        new(Store: true, Apply: ShouldApply(incoming, lastAppliedUpdatedAt, lastAppliedHeadSha));

    /// <summary>
    /// False when <c>incoming.UpdatedAt</c> is older than the last applied update for the PR.
    /// Equal timestamps with two different known head SHAs are also not applied, because their order is unknown;
    /// the next event with newer state settles it. Replays are removed earlier by <see cref="GitHubEventDedupe"/>.
    /// </summary>
    public static bool ShouldApply(GitHubEvent incoming, DateTimeOffset? lastAppliedUpdatedAt, string? lastAppliedHeadSha)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (lastAppliedUpdatedAt is not { } last)
        {
            return true;
        }

        if (incoming.UpdatedAt < last)
        {
            return false;
        }

        var shaDiffers = incoming.HeadSha is not null
            && lastAppliedHeadSha is not null
            && incoming.HeadSha != lastAppliedHeadSha;

        return !(incoming.UpdatedAt == last && shaDiffers);
    }
}
