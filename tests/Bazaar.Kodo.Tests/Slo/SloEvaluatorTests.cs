using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Reports;
using Bazaar.Kodo.Slo;
using Bazaar.Kodo.Tests.Reports;

namespace Bazaar.Kodo.Tests.Slo;

public sealed class SloEvaluatorTests
{
    private static SloStatus Eval(SloDefinition definition, ISloSampleStore store, SloBurnPolicy? policy = null) =>
        SloEvaluator.Evaluate(definition, store, policy ?? SloBurnPolicy.Default, SloTestKit.Now);

    [Fact]
    public void A_series_with_no_sample_has_no_data_and_no_budget_left()
    {
        SloStatus status = Eval(SloTestKit.Availability(), new InMemorySloSampleStore());

        Assert.Equal(SloHealth.NoData, status.Health);
        Assert.Equal(0, status.RemainingBudgetPpm);
        Assert.Null(status.AchievedPpm);
        Assert.Equal(0, status.TotalUnits);
        Assert.All(status.BurnRules, r => Assert.False(r.Firing));
        Assert.Equal(0, SloReportAdapter.ToReportSlo(status).RemainingBudgetPermille);
    }

    [Fact]
    public void All_good_keeps_the_whole_budget()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);

        SloStatus status = Eval(SloTestKit.Availability(), store);

        Assert.Equal(SloHealth.Ok, status.Health);
        Assert.Equal(60, status.GoodUnits);
        Assert.Equal(1_000_000, status.AchievedPpm);
        Assert.Equal(0, status.ConsumedBudgetPpm);
        Assert.Equal(1_000_000, status.RemainingBudgetPpm);
    }

    [Fact]
    public void The_budget_is_the_allowed_share_of_the_window()
    {
        // 95 percent over 60 slots allows 3 bad slots.
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);
        SloTestKit.Put(store, "svc", 30, SloSampleOutcome.Bad);

        SloStatus one = Eval(SloTestKit.Availability(), store);

        // The bad sample shares slot 30 with a good one: worst wins, so the slot is bad.
        Assert.Equal(59, one.GoodUnits);
        Assert.Equal(1, one.BadUnits);
        Assert.Equal(333_333, one.ConsumedBudgetPpm);
        Assert.Equal(666_667, one.RemainingBudgetPpm);
        Assert.Equal(SloHealth.Ok, one.Health);

        SloTestKit.Put(store, "svc", 31, SloSampleOutcome.Bad);
        SloTestKit.Put(store, "svc", 32, SloSampleOutcome.Bad);
        SloStatus three = Eval(SloTestKit.Availability(), store);

        Assert.Equal(1_000_000, three.ConsumedBudgetPpm);
        Assert.Equal(0, three.RemainingBudgetPpm);
        Assert.Equal(SloHealth.Breached, three.Health);
    }

    [Fact]
    public void Overspending_is_reported_and_remaining_stays_at_zero()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Bad);

        SloStatus status = Eval(SloTestKit.Availability(), store);

        Assert.Equal(20_000_000, status.ConsumedBudgetPpm);
        Assert.Equal(0, status.RemainingBudgetPpm);
        Assert.Equal(0, status.AchievedPpm);
    }

    [Fact]
    public void Unobserved_time_consumes_budget_and_is_counted_apart()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);
        SloTestKit.Put(store, "svc", 20, SloSampleOutcome.Unobserved);

        SloStatus status = Eval(SloTestKit.Availability(), store);

        Assert.Equal(0, status.BadUnits);
        Assert.Equal(1, status.UnobservedUnits);
        Assert.Equal(59, status.GoodUnits);
        Assert.Equal(333_333, status.ConsumedBudgetPpm);
    }

    [Fact]
    public void Missing_slots_after_the_first_sample_are_unobserved_but_time_before_it_is_not()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 51, SloSampleOutcome.Good);

        // Slots 50 down to 1 are empty: 50 gaps. The series started 60 slots ago, so there is nothing before it to count.
        SloStatus gaps = Eval(SloTestKit.Availability(), store);
        Assert.Equal(10, gaps.GoodUnits);
        Assert.Equal(50, gaps.UnobservedUnits);

        InMemorySloSampleStore young = new();
        SloTestKit.PutRange(young, "svc", 5, 1, SloSampleOutcome.Good);
        SloStatus fresh = Eval(SloTestKit.Availability(), young);
        Assert.Equal(5, fresh.TotalUnits);
        Assert.Equal(0, fresh.UnobservedUnits);
        Assert.Equal(1_000_000, fresh.RemainingBudgetPpm);
    }

    [Fact]
    public void The_slot_in_progress_never_counts()
    {
        InMemorySloSampleStore store = new();
        Assert.True(store.Record("svc", SloTestKit.Now + TimeSpan.FromSeconds(10), SloSampleOutcome.Bad));

        Assert.Equal(SloHealth.NoData, Eval(SloTestKit.Availability(), store).Health);
    }

    [Fact]
    public void Samples_older_than_the_window_do_not_count()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.Put(store, "svc", 61, SloSampleOutcome.Bad);
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);

        SloStatus status = Eval(SloTestKit.Availability(), store);

        Assert.Equal(60, status.GoodUnits);
        Assert.Equal(0, status.BadUnits);
    }

    [Fact]
    public void A_latency_slo_counts_samples_and_ignores_empty_slots()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.Put(store, "lat", 10, SloSampleOutcome.Good, 98);
        SloTestKit.Put(store, "lat", 3, SloSampleOutcome.Bad, 2);

        SloStatus status = Eval(SloTestKit.Latency(), store);

        Assert.Equal(98, status.GoodUnits);
        Assert.Equal(2, status.BadUnits);
        Assert.Equal(0, status.UnobservedUnits);
        Assert.Equal(980_000, status.AchievedPpm);
        Assert.Equal(400_000, status.ConsumedBudgetPpm);
        Assert.Equal(600_000, status.RemainingBudgetPpm);
    }

    [Fact]
    public void Unobserved_latency_samples_are_never_good()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.Put(store, "lat", 10, SloSampleOutcome.Good, 90);
        SloTestKit.Put(store, "lat", 10, SloSampleOutcome.Unobserved, 10);

        SloStatus status = Eval(SloTestKit.Latency(), store);

        Assert.Equal(10, status.UnobservedUnits);
        Assert.Equal(900_000, status.AchievedPpm);
        Assert.Equal(0, status.RemainingBudgetPpm);
        Assert.Equal(SloHealth.Breached, status.Health);
    }

    [Fact]
    public void A_short_hot_spike_does_not_fire_because_the_long_window_stays_calm()
    {
        SloDefinition definition = SloTestKit.Availability(990_000, TimeSpan.FromDays(30));
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 360, 6, SloSampleOutcome.Good);
        SloTestKit.PutRange(store, "svc", 5, 1, SloSampleOutcome.Bad);

        SloStatus status = Eval(definition, store);

        // Short: 5 of 60 over 1 percent is 8.33 times. Long: 5 of 360 over 1 percent is 1.38 times, under both thresholds.
        SloBurnReading fast = status.BurnRules.Single(r => r.RuleName == "fast_burn");
        Assert.Equal(8_333, fast.ShortBurnMilli);
        Assert.Equal(1_388, fast.LongBurnMilli);
        Assert.False(fast.Firing);
        Assert.Equal(SloHealth.Ok, status.Health);
    }

    [Fact]
    public void A_sustained_moderate_burn_is_a_warning()
    {
        SloDefinition definition = SloTestKit.Availability(990_000, TimeSpan.FromDays(30));
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 360, 61, SloSampleOutcome.Good);
        SloTestKit.PutRange(store, "svc", 60, 11, SloSampleOutcome.Good);
        SloTestKit.PutRange(store, "svc", 10, 1, SloSampleOutcome.Bad);

        SloStatus status = Eval(definition, store);

        SloBurnReading slow = status.BurnRules.Single(r => r.RuleName == "slow_burn");
        Assert.Equal(16_666, slow.ShortBurnMilli);
        Assert.Equal(2_777, slow.LongBurnMilli);
        Assert.True(slow.Firing);
        Assert.False(status.BurnRules.Single(r => r.RuleName == "fast_burn").Firing);
        Assert.Equal(SloHealth.Warning, status.Health);
        Assert.Single(status.FiringRules);
    }

    [Fact]
    public void A_severe_sustained_burn_is_critical_and_breached_even_with_budget_left()
    {
        SloDefinition definition = SloTestKit.Availability(990_000, TimeSpan.FromDays(30));
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 360, 1, SloSampleOutcome.Bad);

        SloStatus status = Eval(definition, store);

        Assert.All(status.BurnRules, r => Assert.True(r.Firing));
        Assert.Equal(SloHealth.Breached, status.Health);
        Assert.Equal(100_000, status.BurnRules[0].ShortBurnMilli);
    }

    [Fact]
    public void A_tightened_policy_fires_sooner()
    {
        SloDefinition definition = SloTestKit.Availability(990_000, TimeSpan.FromDays(30));
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 360, 6, SloSampleOutcome.Good);
        SloTestKit.PutRange(store, "svc", 5, 1, SloSampleOutcome.Bad);
        Assert.True(SloBurnPolicy.TryTighten(
            [
                new SloBurnRule("fast_burn", SloBurnSeverity.Critical, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60), 6_000),
                new SloBurnRule("slow_burn", SloBurnSeverity.Warning, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(360), 1_000),
            ],
            out SloBurnPolicy? tight));

        SloStatus status = Eval(definition, store, tight);

        Assert.True(status.BurnRules.Single(r => r.RuleName == "fast_burn").Firing);
        Assert.Equal(SloHealth.Breached, status.Health);
    }

    [Fact]
    public void The_same_inputs_give_the_same_status()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);
        SloTestKit.Put(store, "svc", 7, SloSampleOutcome.Bad);

        SloStatus a = Eval(SloTestKit.Availability(), store);
        SloStatus b = Eval(SloTestKit.Availability(), store);

        Assert.Equal(a with { BurnRules = [] }, b with { BurnRules = [] });
        Assert.Equal(a.BurnRules, b.BurnRules);
        Assert.Equal(SloTestKit.Now, a.EvaluatedAt);
    }

    [Fact]
    public void A_store_too_small_for_the_window_is_a_caller_error()
    {
        InMemorySloSampleStore small = new(maxSlots: 10);

        Assert.Throws<ArgumentException>(() => Eval(SloTestKit.Availability(), small));
    }

    [Fact]
    public void The_snapshot_is_sorted_and_duplicates_are_refused()
    {
        InMemorySloSampleStore store = new();
        SloDefinition b = new("b_svc", 950_000, TimeSpan.FromHours(1), SloIndicatorKind.Availability);
        SloDefinition a = new("a_svc", 950_000, TimeSpan.FromHours(1), SloIndicatorKind.Availability);

        SloSnapshot snapshot = SloEvaluator.EvaluateAll([b, a], store, SloBurnPolicy.Default, SloTestKit.Now);

        Assert.Equal(["a_svc", "b_svc"], snapshot.Statuses.Select(s => s.Id));
        Assert.True(snapshot.AnyNoData);
        Assert.Throws<ArgumentException>(() => SloEvaluator.EvaluateAll([a, a], store, SloBurnPolicy.Default, SloTestKit.Now));
    }

    [Fact]
    public void The_snapshot_raises_the_evaluator_flags_without_lowering_any()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Bad);
        SloSnapshot bad = SloEvaluator.EvaluateAll([SloTestKit.Availability()], store, SloBurnPolicy.Default, SloTestKit.Now);

        KodoObservation raised = bad.ApplyTo(new KodoObservation());
        Assert.True(raised.SloWarningBurn);
        Assert.True(raised.SloBreach);

        SloSnapshot empty = SloEvaluator.EvaluateAll([], store, SloBurnPolicy.Default, SloTestKit.Now);
        KodoObservation kept = empty.ApplyTo(new KodoObservation { SloBreach = true });
        Assert.True(kept.SloBreach);
        Assert.False(kept.SloWarningBurn);
    }

    [Fact]
    public void The_report_adapter_rounds_down_so_a_report_never_overstates()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);
        SloTestKit.Put(store, "svc", 30, SloSampleOutcome.Bad);
        SloSnapshot snapshot = SloEvaluator.EvaluateAll(
            [SloTestKit.Availability(995_500), new SloDefinition("zz", 950_000, TimeSpan.FromHours(1), SloIndicatorKind.Availability)],
            store,
            SloBurnPolicy.Default,
            SloTestKit.Now);

        IReadOnlyList<ReportSlo> lines = SloReportAdapter.ToReportSlos(snapshot);

        Assert.Equal(2, lines.Count);
        Assert.Equal("svc", lines[0].Id);
        Assert.Equal(995, lines[0].TargetPermille);
        Assert.Equal(snapshot.Statuses[0].RemainingBudgetPpm / 1_000, lines[0].RemainingBudgetPermille);
        // No data for the second one: no budget is reported.
        Assert.Equal(0, lines[1].RemainingBudgetPermille);
    }

    [Fact]
    public void The_report_builder_accepts_the_adapter_output()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.PutRange(store, "svc", 60, 1, SloSampleOutcome.Good);
        SloSnapshot snapshot = SloEvaluator.EvaluateAll([SloTestKit.Availability()], store, SloBurnPolicy.Default, SloTestKit.Now);
        ReportInputs inputs = ReportTestKit.Inputs(slos: SloReportAdapter.ToReportSlos(snapshot));

        HealthReport report = HealthReportBuilder.Build(
            ReportTestKit.Definition(), ReportTestKit.Utc(2026, 11, 21, 6), ReportTestKit.Utc(2026, 11, 22, 6), inputs, ReportTestKit.Now);

        ReportSloEntry entry = Assert.Single(report.Slos);
        Assert.Equal(new ReportSloEntry("svc", 950, 1000), entry);
        Assert.Equal(0, report.Omitted);
    }
}
