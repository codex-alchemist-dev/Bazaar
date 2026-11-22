using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Gateway.Selection.Tests;

public class SelectorTests
{
    private static readonly EndpointKey A = new("anthropic-direct", "claude-haiku-5-5");
    private static readonly EndpointKey B = new("freellmapi", "claude-haiku-5-5");
    private static readonly EndpointKey C = new("claude-code", "claude-haiku-5-5");

    private sealed class Signals : IExternalSignalSource
    {
        public Dictionary<EndpointKey, ExternalSignals> Map { get; } = [];

        public ExternalSignals GetSignals(EndpointKey key) => Map.GetValueOrDefault(key, ExternalSignals.None);
    }

    private static Endpoint Ep(EndpointKey key, int tier = 0, string identity = "native") => new(key) { Tier = tier, Identity = identity };

    private static (EndpointSelector Selector, EndpointStatsTracker Stats, FakeTimeProvider Time, Signals Signals) Make(
        SelectionProfile? profile = null, int seed = 1)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));
        var stats = new EndpointStatsTracker(time: time);
        var signals = new Signals();
        var profiles = profile is null ? null : new[] { profile };
        var selector = new EndpointSelector(stats, signals, profiles, profile?.Name ?? SelectionProfiles.Balanced, new SeededSelectionRandom(seed), time);
        return (selector, stats, time, signals);
    }

    private static SelectionProfile Best(string name = "t", double explore = 0) =>
        new(name, new ScoreWeights(), SelectionStrategy.Best) { ExploreFraction = explore };

    private static void Succeed(EndpointStatsTracker stats, EndpointKey key, int ms, int times = 10, string? affinity = null)
    {
        for (var i = 0; i < times; i++)
        {
            using var attempt = stats.Begin(key, affinity);
            attempt.Complete(new AttemptOutcome(AttemptOutcomeKind.Success, TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(ms)));
        }
    }

    private static void Fail(EndpointStatsTracker stats, EndpointKey key, int times)
    {
        for (var i = 0; i < times; i++)
        {
            using var attempt = stats.Begin(key);
            attempt.Complete(new AttemptOutcome(AttemptOutcomeKind.Failure));
        }
    }

    [Fact]
    public void Ranking_only_holds_endpoints_the_caller_passed_in()
    {
        var (selector, _, _, _) = Make(SelectionProfiles.Defaults[0]);
        var input = new[] { Ep(A), Ep(B), Ep(C) };
        for (var i = 0; i < 200; i++)
        {
            var d = selector.Rank(input, new SelectionRequest());
            Assert.All(d.Ranking, r => Assert.Contains(r.Endpoint, input));
            Assert.Equal(3, d.Ranking.Select(r => r.Endpoint.Key).Distinct().Count());
        }
    }

    [Fact]
    public void Empty_input_gives_empty_ranking()
    {
        var (selector, _, _, _) = Make();
        Assert.Empty(selector.Rank([], new SelectionRequest()).Ranking);
    }

    [Fact]
    public void Hard_filters_carry_reasons()
    {
        var (selector, _, time, signals) = Make(Best());
        signals.Map[B] = new ExternalSignals { BreakerOpen = true };
        signals.Map[C] = new ExternalSignals { QuotaBlockedUntil = time.GetUtcNow().AddMinutes(1) };
        var d = selector.Rank(
            [Ep(A) with { Capabilities = EndpointCapabilities.Tools }, Ep(B), Ep(C), Ep(new EndpointKey("x", "small")) with { ContextWindowTokens = 100, Capabilities = EndpointCapabilities.Tools }, Ep(new EndpointKey("x", "none"))],
            new SelectionRequest { Required = EndpointCapabilities.Tools, EstimatedInputTokens = 500, Excluded = new HashSet<EndpointKey>() });
        Assert.Equal([A], d.Ranking.Select(r => r.Endpoint.Key));
        Assert.Contains(d.Rejected, r => r.Endpoint.Key == new EndpointKey("x", "small") && r.Reason == RejectionReason.ContextTooSmall);
        Assert.Contains(d.Rejected, r => r.Endpoint.Key == new EndpointKey("x", "none") && r.Reason == RejectionReason.MissingCapability);
    }

    [Fact]
    public void Breaker_and_quota_reasons_apply_when_capability_is_met()
    {
        var (selector, _, time, signals) = Make(Best());
        signals.Map[B] = new ExternalSignals { BreakerOpen = true };
        signals.Map[C] = new ExternalSignals { QuotaBlockedUntil = time.GetUtcNow().AddMinutes(1) };
        var d = selector.Rank([Ep(A), Ep(B), Ep(C)], new SelectionRequest());
        Assert.Equal([A], d.Ranking.Select(r => r.Endpoint.Key));
        Assert.Equal(RejectionReason.BreakerOpen, d.Rejected.Single(r => r.Endpoint.Key == B).Reason);
        Assert.Equal(RejectionReason.QuotaBlocked, d.Rejected.Single(r => r.Endpoint.Key == C).Reason);
    }

    [Fact]
    public void Expired_quota_block_no_longer_rejects()
    {
        var (selector, _, time, signals) = Make(Best());
        signals.Map[A] = new ExternalSignals { QuotaBlockedUntil = time.GetUtcNow().AddSeconds(-1) };
        Assert.Single(selector.Rank([Ep(A)], new SelectionRequest()).Ranking);
    }

    [Fact]
    public void Excluded_endpoint_is_not_ranked()
    {
        var (selector, _, _, _) = Make(Best());
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest { Excluded = new HashSet<EndpointKey> { A } });
        Assert.Equal([B], d.Ranking.Select(r => r.Endpoint.Key));
    }

    [Fact]
    public void Better_tier_wins_and_lower_tier_backs_it_up()
    {
        var (selector, stats, _, _) = Make(Best());
        Succeed(stats, A, 5000);
        Succeed(stats, B, 10);
        var d = selector.Rank([Ep(A, tier: 0), Ep(B, tier: 1)], new SelectionRequest());
        Assert.Equal([A, B], d.Ranking.Select(r => r.Endpoint.Key));
    }

    [Fact]
    public void Soft_tiers_let_a_much_better_lower_tier_win()
    {
        var profile = Best() with { SoftTiers = true, TierPenalty = 0.1 };
        var (selector, stats, _, _) = Make(profile);
        Succeed(stats, A, 8000);
        Succeed(stats, B, 100);
        var d = selector.Rank([Ep(A, tier: 0), Ep(B, tier: 1)], new SelectionRequest());
        Assert.Equal(B, d.Ranking[0].Endpoint.Key);
    }

    [Fact]
    public void Faster_endpoint_scores_higher()
    {
        var (selector, stats, _, _) = Make(Best());
        Succeed(stats, A, 900);
        Succeed(stats, B, 200);
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest());
        Assert.Equal(B, d.Ranking[0].Endpoint.Key);
        Assert.True(d.Ranking[0].Score.Latency > d.Ranking[1].Score.Latency);
    }

    [Fact]
    public void In_flight_calls_push_work_elsewhere()
    {
        var (selector, stats, _, _) = Make(Best());
        Succeed(stats, A, 200);
        Succeed(stats, B, 300);
        var held = Enumerable.Range(0, 16).Select(_ => stats.Begin(A)).ToList();
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest());
        Assert.Equal(B, d.Ranking[0].Endpoint.Key);
        held.ForEach(h => h.Dispose());
    }

    [Fact]
    public void Failing_endpoint_loses_to_a_reliable_one()
    {
        var (selector, stats, _, _) = Make(Best());
        Succeed(stats, A, 300);
        Succeed(stats, B, 300);
        Fail(stats, A, 12);
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest());
        Assert.Equal(B, d.Ranking[0].Endpoint.Key);
    }

    [Fact]
    public void Throttled_and_abandoned_attempts_do_not_count_as_failures()
    {
        var (_, stats, _, _) = Make();
        stats.Begin(A).Complete(new AttemptOutcome(AttemptOutcomeKind.Throttled));
        stats.Begin(A).Complete(new AttemptOutcome(AttemptOutcomeKind.Abandoned));
        using (stats.Begin(A))
        {
        }

        var s = stats.Snapshot(A);
        Assert.Equal(0, s.Failures);
        Assert.Equal(0, s.InFlight);
    }

    [Fact]
    public void Old_failures_fade_when_an_endpoint_is_idle()
    {
        var (_, stats, time, _) = Make();
        Fail(stats, A, 10);
        var before = stats.Snapshot(A).Failures;
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.True(stats.Snapshot(A).Failures < before / 10);
    }

    [Fact]
    public void Latency_spike_counts_at_once_then_fades()
    {
        var (_, stats, time, _) = Make();
        Succeed(stats, A, 100, 5);
        Succeed(stats, A, 4000, 1);
        Assert.True(stats.Snapshot(A).PeakEwmaLatency >= TimeSpan.FromMilliseconds(4000));
        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            Succeed(stats, A, 100, 1);
        }

        Assert.True(stats.Snapshot(A).PeakEwmaLatency < TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void Native_comes_before_pooled_by_default_and_the_order_can_flip()
    {
        var native = Ep(A, identity: "native");
        var pooled = Ep(B, identity: "pooled");
        var (selector, _, _, _) = Make(Best());
        Assert.Equal(A, selector.Rank([pooled, native], new SelectionRequest()).Ranking[0].Endpoint.Key);
        var (flipped, _, _, _) = Make(Best() with { NativeFirst = false });
        Assert.Equal(B, flipped.Rank([native, pooled], new SelectionRequest()).Ranking[0].Endpoint.Key);
    }

    [Fact]
    public void Cost_profile_prefers_the_cheaper_endpoint_but_never_blocks_the_dearer()
    {
        var cheap = Ep(A) with { Price = new EndpointPrice(1, 5) };
        var dear = Ep(B) with { Price = new EndpointPrice(15, 75) };
        var (selector, _, _, _) = Make(SelectionProfiles.Defaults.Single(p => p.Name == SelectionProfiles.Cost) with { Strategy = SelectionStrategy.Best, ExploreFraction = 0 });
        var d = selector.Rank([dear, cheap], new SelectionRequest { EstimatedInputTokens = 1000, ExpectedOutputTokens = 500 }with { Profile = SelectionProfiles.Cost });
        Assert.Equal([A, B], d.Ranking.Select(r => r.Endpoint.Key));
    }

    [Fact]
    public void Low_quota_headroom_lowers_the_score()
    {
        var (selector, _, _, signals) = Make(Best());
        signals.Map[A] = new ExternalSignals { QuotaHeadroom = 0.02 };
        signals.Map[B] = new ExternalSignals { QuotaHeadroom = 0.9 };
        Assert.Equal(B, selector.Rank([Ep(A), Ep(B)], new SelectionRequest()).Ranking[0].Endpoint.Key);
    }

    [Fact]
    public void Affinity_keeps_a_warm_cache_endpoint_on_top()
    {
        var (selector, stats, _, _) = Make(Best());
        Succeed(stats, A, 300, 3);
        Succeed(stats, B, 290, 3, affinity: "agent-1/convo-9");
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest { AffinityKey = "agent-1/convo-9" });
        Assert.Equal(B, d.Ranking[0].Endpoint.Key);
        var other = selector.Rank([Ep(A), Ep(B)], new SelectionRequest { AffinityKey = "agent-2/convo-1" });
        Assert.Equal(1.0, d.Ranking[0].Score.Affinity);
        Assert.All(other.Ranking, r => Assert.Equal(0.0, r.Score.Affinity));
    }

    [Fact]
    public void Affinity_expires()
    {
        var (_, stats, time, _) = Make();
        Succeed(stats, A, 100, 1, affinity: "k");
        Assert.Equal(A, stats.AffinityHolder("k"));
        time.Advance(TimeSpan.FromHours(2));
        Assert.Null(stats.AffinityHolder("k"));
    }

    [Fact]
    public void Quality_prior_per_task_class_overrides_the_default()
    {
        var profile = new SelectionProfile("q", new ScoreWeights { Latency = 0, Reliability = 0, Headroom = 0, Cost = 0, Identity = 0, Affinity = 0, Quality = 1 }, SelectionStrategy.Best) { ExploreFraction = 0 };
        var (selector, _, _, _) = Make(profile);
        var a = Ep(A) with { Quality = 0.9, QualityByTask = new Dictionary<string, double> { ["code"] = 0.2 } };
        var b = Ep(B) with { Quality = 0.5 };
        Assert.Equal(A, selector.Rank([a, b], new SelectionRequest { TaskClass = "chat" }).Ranking[0].Endpoint.Key);
        Assert.Equal(B, selector.Rank([a, b], new SelectionRequest { TaskClass = "code" }).Ranking[0].Endpoint.Key);
    }

    [Fact]
    public void Power_of_two_never_picks_the_worse_of_its_pair_for_the_top_slot()
    {
        var profile = new SelectionProfile("p2", new ScoreWeights(), SelectionStrategy.PowerOfTwo) { ExploreFraction = 0 };
        var (selector, stats, _, _) = Make(profile, seed: 7);
        Succeed(stats, A, 100);
        Succeed(stats, B, 5000);
        var topA = 0;
        for (var i = 0; i < 500; i++)
        {
            // With two endpoints the pair is always both, so the better one must always lead.
            if (selector.Rank([Ep(A), Ep(B)], new SelectionRequest()).Ranking[0].Endpoint.Key == A)
            {
                topA++;
            }
        }

        Assert.Equal(500, topA);
    }

    [Fact]
    public void Power_of_two_spreads_load_across_three_good_endpoints()
    {
        var profile = new SelectionProfile("p2", new ScoreWeights(), SelectionStrategy.PowerOfTwo) { ExploreFraction = 0 };
        var (selector, stats, _, _) = Make(profile, seed: 3);
        foreach (var k in new[] { A, B, C })
        {
            Succeed(stats, k, 200);
        }

        var firsts = Enumerable.Range(0, 600).Select(_ => selector.Rank([Ep(A), Ep(B), Ep(C)], new SelectionRequest()).Ranking[0].Endpoint.Key).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, firsts.Count);
        Assert.All(firsts.Values, v => Assert.InRange(v, 100, 400));
    }

    [Fact]
    public void Thompson_favours_evidence()
    {
        var profile = new SelectionProfile("ts", new ScoreWeights { Latency = 0, Reliability = 1, Headroom = 0, Cost = 0, Identity = 0, Affinity = 0, Quality = 0 }, SelectionStrategy.Thompson) { ExploreFraction = 0 };
        var (selector, stats, _, _) = Make(profile, seed: 11);
        Succeed(stats, A, 100, 60);
        Succeed(stats, B, 100, 5);
        Fail(stats, B, 20);
        var aFirst = Enumerable.Range(0, 300).Count(_ => selector.Rank([Ep(A), Ep(B)], new SelectionRequest()).Ranking[0].Endpoint.Key == A);
        Assert.True(aFirst > 290, $"A led only {aFirst}/300");
    }

    [Fact]
    public void Weighted_random_uses_every_endpoint_and_returns_all_of_them()
    {
        var profile = new SelectionProfile("wr", new ScoreWeights(), SelectionStrategy.WeightedRandom) { ExploreFraction = 0 };
        var (selector, _, _, _) = Make(profile, seed: 5);
        var firsts = new HashSet<EndpointKey>();
        for (var i = 0; i < 100; i++)
        {
            var d = selector.Rank([Ep(A), Ep(B), Ep(C)], new SelectionRequest());
            Assert.Equal(3, d.Ranking.Count);
            firsts.Add(d.Ranking[0].Endpoint.Key);
        }

        Assert.Equal(3, firsts.Count);
    }

    [Fact]
    public void Same_seed_gives_the_same_order()
    {
        var profile = new SelectionProfile("p2", new ScoreWeights(), SelectionStrategy.PowerOfTwo);
        var one = Make(profile, 42).Selector.Rank([Ep(A), Ep(B), Ep(C)], new SelectionRequest());
        var two = Make(profile, 42).Selector.Rank([Ep(A), Ep(B), Ep(C)], new SelectionRequest());
        Assert.Equal(one.Ranking.Select(r => r.Endpoint.Key), two.Ranking.Select(r => r.Endpoint.Key));
    }

    [Fact]
    public void Explore_fraction_sometimes_swaps_the_first_two()
    {
        var profile = Best() with { ExploreFraction = 0.5 };
        var (selector, stats, _, _) = Make(profile, 9);
        Succeed(stats, A, 100);
        Succeed(stats, B, 4000);
        var swapped = Enumerable.Range(0, 200).Count(_ => selector.Rank([Ep(A), Ep(B)], new SelectionRequest()).Ranking[0].Endpoint.Key == B);
        Assert.InRange(swapped, 60, 140);
    }

    [Fact]
    public void Hedge_plan_needs_opt_in_and_a_p95()
    {
        var (selector, stats, _, _) = Make(Best());
        Succeed(stats, A, 300, 10);
        Succeed(stats, B, 900, 10);
        Assert.Null(selector.Rank([Ep(A), Ep(B)], new SelectionRequest()).Hedge);
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest { AllowHedge = true });
        Assert.NotNull(d.Hedge);
        Assert.Equal(A, d.Hedge.Primary.Key);
        Assert.Equal(B, d.Hedge.Secondary.Key);
        Assert.Equal(TimeSpan.FromMilliseconds(300), d.Hedge.After);
        var (fresh, _, _, _) = Make(Best());
        Assert.Null(fresh.Rank([Ep(A), Ep(B)], new SelectionRequest { AllowHedge = true }).Hedge);
    }

    [Fact]
    public void Trace_code_is_short_and_secret_free()
    {
        var (selector, _, _, _) = Make(Best("lat"));
        var d = selector.Rank([Ep(A), Ep(B)], new SelectionRequest());
        Assert.Equal("lat/best/n2", d.TraceCode);
    }

    [Fact]
    public async Task Ranker_returns_null_for_an_unbound_route_and_refuses_an_unknown_profile()
    {
        var (selector, _, _, _) = Make(Best());
        var ranker = new ProfileBoundRanker(selector);
        Assert.Null(ranker.Order("claude", [Ep(A), Ep(B)], new SelectionRequest()));
        await Assert.ThrowsAsync<ArgumentException>(() => ranker.BindAsync("claude", "nope", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ranker_orders_bound_routes_and_keeps_blocked_endpoints_last()
    {
        var ct = TestContext.Current.CancellationToken;
        var (selector, stats, _, signals) = Make(Best());
        var ranker = new ProfileBoundRanker(selector);
        Succeed(stats, A, 900);
        Succeed(stats, B, 100);
        signals.Map[C] = new ExternalSignals { BreakerOpen = true };
        await ranker.BindAsync("claude", SelectionProfiles.Latency, ct);
        var input = new[] { Ep(A), Ep(C), Ep(B), Ep(new EndpointKey("x", "needs-tools")) };
        var ordered = ranker.Order("claude", input, new SelectionRequest())!;
        Assert.Equal(B, ordered[0].Key);
        Assert.Equal(C, ordered[^1].Key);
        Assert.All(ordered, e => Assert.Contains(e, input));
        var capable = ranker.Order("claude", input, new SelectionRequest { Required = EndpointCapabilities.Tools })!;
        Assert.Empty(capable);
        Assert.Contains("balanced", await ranker.ListProfilesAsync(ct));
    }

    [Fact]
    public async Task Concurrent_feedback_is_consistent()
    {
        var (_, stats, _, _) = Make();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Succeed(stats, A, 100, 250))));
        var s = stats.Snapshot(A);
        Assert.Equal(0, s.InFlight);
        Assert.Equal(2000, s.Successes, 1);
    }
}
