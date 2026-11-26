using Bazaar.Server.OutcomeLedger;
using Bazaar.Server.OutcomeLedger.Abstractions;
using Bazaar.Server.OutcomeLedger.GitHub;

namespace Bazaar.Server.OutcomeLedger.GitHub.Tests;

public class GitHubOutcomeIngestTests
{
    private static readonly DateTimeOffset _merged = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Resolver(params OutcomeAttribution[] who) : IPrAttributionResolver
    {
        public Task<IReadOnlyList<OutcomeAttribution>> ResolveAsync(string repo, int pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<OutcomeAttribution>>(who);
    }

    private static (GitHubOutcomeIngest Ingest, InMemoryOutcomeLedgerStore Store) Build(bool agentPr = true)
    {
        var store = new InMemoryOutcomeLedgerStore();
        var recorder = new OutcomeRecorder(store, new OutcomeWeights(), TimeProvider.System, []);
        var resolver = agentPr ? new Resolver(new OutcomeAttribution("a1", "author", "csharp")) : new Resolver();
        return (new GitHubOutcomeIngest(recorder, resolver, new GitHubOutcomeOptions()), store);
    }

    [Fact]
    public async Task A_webhook_and_a_poll_of_the_same_check_run_count_once()
    {
        var (ingest, store) = Build();
        var webhook = new CheckRunFinished("org/r", 5, 111, false, _merged, "hook:delivery-1");
        var poll = new CheckRunFinished("org/r", 5, 111, false, _merged, "poll:event-9");

        await ingest.IngestAsync(webhook, TestContext.Current.CancellationToken);
        var second = await ingest.IngestAsync(poll, TestContext.Current.CancellationToken);

        Assert.False(second!.Recorded);
        Assert.Single(store.Rows);
        Assert.Equal(0m, store.Rows[0].Record.X);
        Assert.Equal(OutcomeKind.CiFirstRun, store.Rows[0].Record.Kind);
    }

    [Fact]
    public async Task Only_the_first_run_of_a_pr_counts()
    {
        var (ingest, store) = Build();
        await ingest.IngestAsync(new CheckRunFinished("org/r", 5, 1, false, _merged), TestContext.Current.CancellationToken);
        await ingest.IngestAsync(new CheckRunFinished("org/r", 5, 2, true, _merged.AddMinutes(9)), TestContext.Current.CancellationToken);

        Assert.Single(store.Rows);
        Assert.Equal(0m, store.Rows[0].Record.X);
    }

    [Fact]
    public async Task A_revert_inside_the_window_is_a_failure_and_one_outside_is_not_evidence()
    {
        var (ingest, store) = Build();
        var inside = await ingest.IngestAsync(new PullRequestReverted("org/r", 5, 9, _merged, _merged.AddDays(3)), TestContext.Current.CancellationToken);
        var outside = await ingest.IngestAsync(new PullRequestReverted("org/r", 6, 10, _merged, _merged.AddDays(15)), TestContext.Current.CancellationToken);

        Assert.True(inside!.Recorded);
        Assert.Null(outside);
        Assert.Single(store.Rows);
        Assert.Equal(OutcomeKind.Revert, store.Rows[0].Record.Kind);
        Assert.Equal(0m, store.Rows[0].Record.X);
    }

    [Fact]
    public async Task A_defect_links_by_its_own_pr_and_the_same_link_counts_once()
    {
        var (ingest, store) = Build();
        var fact = new PostMergeDefectLinked("org/r", 5, 20, _merged, _merged.AddDays(2));
        await ingest.IngestAsync(fact, TestContext.Current.CancellationToken);
        await ingest.IngestAsync(fact, TestContext.Current.CancellationToken);
        await ingest.IngestAsync(fact with { DefectPullRequest = 21 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Rows.Count);
    }

    [Fact]
    public async Task Owner_decisions_map_to_accept_and_reject()
    {
        var (ingest, store) = Build();
        await ingest.IngestAsync(new OwnerDecided("org/r", 5, true, "card-1", _merged), TestContext.Current.CancellationToken);
        await ingest.IngestAsync(new OwnerDecided("org/r", 6, false, "card-2", _merged), TestContext.Current.CancellationToken);

        Assert.Equal(OutcomeKind.OwnerAccept, store.Rows[0].Record.Kind);
        Assert.Equal(1m, store.Rows[0].Record.X);
        Assert.Equal(OutcomeKind.OwnerReject, store.Rows[1].Record.Kind);
        Assert.Equal(0m, store.Rows[1].Record.X);
    }

    [Fact]
    public async Task A_pr_with_no_agent_is_not_evidence()
    {
        var (ingest, store) = Build(agentPr: false);
        var result = await ingest.IngestAsync(new CheckRunFinished("org/r", 5, 1, false, _merged), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(store.Rows);
    }
}
