using Bazaar.Server.OutcomeLedger.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OutcomeLedger.GitHub;

/// <summary>Windows for late outcomes (doc 11 settings, PROPOSED defaults).</summary>
public sealed class GitHubOutcomeOptions
{
    /// <summary>A revert counts when it lands within this many days of the merge (<c>reputation.revert_window_days</c>).</summary>
    public int RevertWindowDays { get; init; } = 14;

    /// <summary>A defect counts when it is linked within this many days of the merge (doc 11: 14).</summary>
    public int DefectWindowDays { get; init; } = 14;
}

/// <summary>
/// Turns GitHub facts into ledger rows (doc 11, work package 3). The dedupe key comes from the GitHub object, not from the delivery,
/// so a webhook and a poll of the same event produce the same key and count once. This differs from doc 21's
/// <c>github:&lt;delivery&gt;</c> intake key on purpose: that key dedupes the delivery, this one dedupes the evidence (PROPOSED).
/// </summary>
public sealed class GitHubOutcomeIngest(IOutcomeRecorder recorder, IPrAttributionResolver attributions, GitHubOutcomeOptions options)
{
    /// <summary>Records the fact. Returns null when it is not evidence (no agent on the PR, outside the window), else the recorder's result.</summary>
    public async Task<OutcomeRecordResult?> IngestAsync(GitHubOutcomeFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var mapped = Map(fact);
        if (mapped is null)
        {
            return null;
        }

        var who = await attributions.ResolveAsync(fact.Repo, fact.PullRequest, cancellationToken).ConfigureAwait(false);
        if (who.Count == 0)
        {
            return null;
        }

        var record = new OutcomeRecord(mapped.Value.Kind, fact.PrRef, mapped.Value.X, mapped.Value.Key, fact.OccurredAt, who, SourceRef: fact.SourceRef);
        return await recorder.RecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private (OutcomeKind Kind, decimal X, string Key)? Map(GitHubOutcomeFact fact) => fact switch
    {
        // Only the first conclusive run counts, so every run of a PR shares one key and the first one recorded wins.
        CheckRunFinished c => (OutcomeKind.CiFirstRun, c.Succeeded ? 1m : 0m, OutcomeDedupeKey.For("github", $"ci_first_run:{c.PrRef}")),
        PullRequestReverted r when WithinDays(r.MergedAt, r.OccurredAt, options.RevertWindowDays) =>
            (OutcomeKind.Revert, 0m, OutcomeDedupeKey.For("github", $"revert:{r.PrRef}:{r.Repo}#{r.RevertPullRequest}")),
        PostMergeDefectLinked d when WithinDays(d.MergedAt, d.OccurredAt, options.DefectWindowDays) =>
            (OutcomeKind.PostMergeDefect, 0m, OutcomeDedupeKey.For("github", $"defect:{d.PrRef}:{d.Repo}#{d.DefectPullRequest}")),
        OwnerDecided o => (o.Accepted ? OutcomeKind.OwnerAccept : OutcomeKind.OwnerReject, o.Accepted ? 1m : 0m,
            OutcomeDedupeKey.For("owner", $"{o.PrRef}:{o.DecisionId}")),
        _ => null,
    };

    private static bool WithinDays(DateTimeOffset merged, DateTimeOffset at, int days) => at >= merged && at - merged <= TimeSpan.FromDays(days);
}

/// <summary>Composition for the GitHub outcome source.</summary>
public static class GitHubOutcomeRegistration
{
    /// <summary>Registers the ingest. The host (or the PR service) must register an <see cref="IPrAttributionResolver"/>.</summary>
    public static IServiceCollection AddBazaarGitHubOutcomes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new GitHubOutcomeOptions());
        services.AddSingleton<GitHubOutcomeIngest>();
        return services;
    }
}
