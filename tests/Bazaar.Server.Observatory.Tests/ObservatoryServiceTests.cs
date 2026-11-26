using Bazaar.Contracts.Gateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Observatory.Tests;

public class ObservatoryServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeUsage(params GatewayUsageRecord[] rows) : IUsageReader
    {
        public ValueTask<IReadOnlyList<GatewayUsageRecord>> ReadRangeAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<GatewayUsageRecord>>([.. rows.Where(r => r.Timestamp >= start && r.Timestamp < until)]);

        public ValueTask<IReadOnlyList<GatewayUsageRecord>> ReadTurnAsync(string turnId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<GatewayUsageRecord>>([.. rows.Where(r => r.TurnId == turnId)]);
    }

    /// <summary>Prices every call at 0.01 except models listed as unpriced.</summary>
    private sealed class FakePrices(params string[] unpricedModels) : IUsageCostCalculator
    {
        public decimal? Price(GatewayUsageRecord record) => unpricedModels.Contains(record.ModelId) ? null : 0.01m;
    }

    private sealed class FakeLanes(params LaneSnapshot[] lanes) : ILaneReader
    {
        public ValueTask<IReadOnlyList<LaneSnapshot>> ReadAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<LaneSnapshot>>(lanes);
    }

    private static GatewayUsageRecord Row(string agent, string model, GatewayIdentity identity, string? turn = null, long input = 100,
        UsageStatus status = UsageStatus.Success, bool estimated = false, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), at ?? _now, agent, turn, "anthropic", "key1", model, null, identity,
            new GatewayTokenUsage(input, 0, 0, 0, 50, 0), 10, status, null, null, estimated, null);

    private static ObservatoryService Service(IUsageReader usage, IUsageCostCalculator? prices = null, ILaneReader? lanes = null, ObservatoryOptions? options = null) =>
        new(usage, prices ?? new FakePrices(), lanes ?? new FakeLanes(), options ?? new ObservatoryOptions(), new FakeTimeProvider(_now));

    [Fact]
    public async Task Summary_splits_by_agent_model_and_source()
    {
        var svc = Service(new FakeUsage(
            Row("a1", "haiku", GatewayIdentity.Native),
            Row("a1", "sonnet", GatewayIdentity.Pooled),
            Row("a2", "haiku", GatewayIdentity.Native, status: UsageStatus.Error)));

        var s = await svc.SummarizeAsync(_now.AddHours(-1), _now.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(3, s.Total.Calls);
        Assert.Equal(1, s.Total.ErrorCalls);
        Assert.Equal(["a1", "a2"], s.ByAgent.Select(g => g.Key));
        Assert.Equal(2, s.ByAgent[0].Calls);
        Assert.Equal(["anthropic:haiku", "anthropic:sonnet"], s.ByModel.Select(g => g.Key));
        Assert.Equal(["native", "pooled"], s.BySource.Select(g => g.Key));
        Assert.Equal(0.03m, s.Total.CostUsd);
    }

    [Fact]
    public async Task Summary_excludes_rows_outside_the_window()
    {
        var svc = Service(new FakeUsage(Row("a1", "haiku", GatewayIdentity.Native, at: _now.AddDays(-2))));

        var s = await svc.SummarizeAsync(_now.AddHours(-1), _now.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, s.Total.Calls);
    }

    [Fact]
    public async Task Unpriced_call_makes_cost_partial_never_zero()
    {
        var svc = Service(new FakeUsage(Row("a1", "mystery", GatewayIdentity.Pooled), Row("a1", "haiku", GatewayIdentity.Native)), new FakePrices("mystery"));

        var s = await svc.SummarizeAsync(_now.AddHours(-1), _now.AddHours(1), TestContext.Current.CancellationToken);

        Assert.False(s.Total.IsCostKnown);
        Assert.Equal(1, s.Total.UnpricedCalls);
        Assert.Equal(0.01m, s.Total.CostUsd);
    }

    [Fact]
    public async Task Chip_sums_a_turn_and_flags_estimates_and_long_prompts()
    {
        var svc = Service(new FakeUsage(
            Row("a1", "haiku", GatewayIdentity.Native, "t1"),
            Row("a1", "haiku", GatewayIdentity.Native, "t1", input: 200_000, estimated: true),
            Row("a1", "haiku", GatewayIdentity.Native, "t2")));

        var chip = await svc.ChipAsync("t1", TestContext.Current.CancellationToken);

        Assert.NotNull(chip);
        Assert.Equal(2, chip.Calls);
        Assert.Equal(0.02m, chip.CostUsd);
        Assert.True(chip.IsEstimate);
        Assert.True(chip.LongPrompt);
        Assert.Equal(200_050 + 150, chip.TotalTokens);
    }

    [Fact]
    public async Task Chip_cost_is_unknown_when_any_call_is_unpriced_and_missing_turn_gives_null()
    {
        var svc = Service(new FakeUsage(Row("a1", "mystery", GatewayIdentity.Native, "t1")), new FakePrices("mystery"));

        Assert.Null((await svc.ChipAsync("t1", TestContext.Current.CancellationToken))!.CostUsd);
        Assert.Null(await svc.ChipAsync("nope", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(10, 100, QuotaState.Available, true)]
    [InlineData(19, 100, QuotaState.Available, true)]
    [InlineData(20, 100, QuotaState.Available, false)]
    [InlineData(90, 100, QuotaState.RateLimited, true)]
    public async Task Lane_is_near_limit_under_the_percent_or_when_not_available(long remaining, long limit, QuotaState state, bool near)
    {
        var lane = new LaneSnapshot("anthropic", "haiku", state, null, remaining, limit, null, null, null, null);
        var view = (await Service(new FakeUsage(), lanes: new FakeLanes(lane)).LanesAsync(TestContext.Current.CancellationToken)).Single();

        Assert.Equal(near, view.NearLimit);
        Assert.Equal((int)(remaining * 100 / limit), view.RemainingPercent);
    }

    [Fact]
    public async Task Lane_uses_the_lowest_reported_figure_and_handles_none()
    {
        var lanes = new FakeLanes(
            new LaneSnapshot("p", "a", QuotaState.Available, null, 90, 100, 5, 100, 80, 100),
            new LaneSnapshot("p", "b", QuotaState.Available, null, null, null, null, null, null, null));

        var views = await Service(new FakeUsage(), lanes: lanes).LanesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, views[0].RemainingPercent);
        Assert.True(views[0].NearLimit);
        Assert.Null(views[1].RemainingPercent);
        Assert.False(views[1].NearLimit);
    }

    [Fact]
    public async Task Card_covers_today_only_and_labels_cost_as_estimate()
    {
        var svc = Service(new FakeUsage(
            Row("a1", "haiku", GatewayIdentity.Native),
            Row("a1", "haiku", GatewayIdentity.Native, at: _now.AddDays(-1))));

        var card = await svc.CardAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Usage today", "By source", "By agent", "Lanes"], card.Sections.Select(s => s.Title));
        Assert.Equal("1", card.Sections[0].Items.Single(i => i.Label == "Calls").Value);
        Assert.Contains(card.Sections[0].Items, i => i.Label == "Cost (est.)");
    }

    [Fact]
    public async Task Card_estimate_label_can_be_turned_off()
    {
        var card = await Service(new FakeUsage(), options: new ObservatoryOptions { EstimateLabel = false }).CardAsync(TestContext.Current.CancellationToken);

        Assert.Contains(card.Sections[0].Items, i => i.Label == "Cost");
    }

    [Fact]
    public async Task Card_cost_is_unknown_when_every_call_is_unpriced()
    {
        var svc = Service(new FakeUsage(Row("a1", "mystery", GatewayIdentity.Native)), new FakePrices("mystery"));

        var card = await svc.CardAsync(TestContext.Current.CancellationToken);

        Assert.Equal("unknown", card.Sections[0].Items.Single(i => i.Label.StartsWith("Cost", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public void Component_registers_the_service_and_reads_settings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Observatory:NearLimitPercent"] = "35" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IUsageReader>(new FakeUsage());
        services.AddSingleton<IUsageCostCalculator>(new FakePrices());
        services.AddSingleton<ILaneReader>(new FakeLanes());
        new ObservatoryComponent().AddServices(services, config);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IObservatoryService>());
        Assert.Equal(35, provider.GetRequiredService<ObservatoryOptions>().NearLimitPercent);
    }

    [Fact]
    public void Component_rejects_an_out_of_range_near_limit_percent()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Observatory:NearLimitPercent"] = "0" }).Build();

        Assert.Throws<InvalidOperationException>(() => ObservatoryComponent.ReadOptions(config));
    }
}
