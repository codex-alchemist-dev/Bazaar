using System.Reflection;
using System.Text.Json;
using Bazaar.Contracts.Panes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Analytics.Tests;

public class AnalyticsTests
{
    private static readonly DateTimeOffset _to = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly AnalyticsWindow _window = new(_to.AddDays(-28), _to);

    private static AgentWorkEvent E(string agent, AgentWorkEventKind kind, int daysAgo = 1) => new(agent, kind, _to.AddDays(-daysAgo));

    private static readonly AgentWorkEvent[] _events =
    [
        E("amy", AgentWorkEventKind.TaskCompleted), E("amy", AgentWorkEventKind.TaskCompleted),
        E("amy", AgentWorkEventKind.ReviewPassed), E("amy", AgentWorkEventKind.ReviewChangesRequested),
        E("amy", AgentWorkEventKind.PrMerged), E("amy", AgentWorkEventKind.PrReverted),
        E("bob", AgentWorkEventKind.TaskCompleted, daysAgo: 40), // outside the window
        E("bob", AgentWorkEventKind.PrMerged),
    ];

    [Fact]
    public void Computes_throughput_pass_rate_and_revert_rate()
    {
        var m = AgentMetricsCalculator.ForAgent("amy", _events, _window);

        Assert.Equal(2, m.TasksCompleted);
        Assert.Equal(0.5, m.TasksPerWeek);
        Assert.Equal(0.5, m.ReviewPassRate);
        Assert.Equal(1.0, m.RevertRate);
    }

    [Fact]
    public void No_samples_is_a_gap_not_a_zero()
    {
        var m = AgentMetricsCalculator.ForAgent("bob", _events, _window);

        Assert.Null(m.ReviewPassRate);
        Assert.Equal(0.0, m.RevertRate);
        Assert.Null(AgentMetricsCalculator.ForAgent("nobody", _events, _window).RevertRate);
        Assert.Null(AgentMetricsCalculator.ForCompany([], _window).ReviewPassRate);
    }

    [Fact]
    public void Events_outside_the_window_are_ignored()
    {
        Assert.Equal(0, AgentMetricsCalculator.ForAgent("bob", _events, _window).TasksCompleted);
        Assert.Equal(2, AgentMetricsCalculator.ForCompany(_events, _window).TasksCompleted);
    }

    [Fact]
    public void Hints_need_enough_samples_and_speak_of_helping_the_company()
    {
        var few = AgentMetricsCalculator.ForAgent("amy", _events, _window);
        Assert.Empty(few.CompanyHints);

        var many = Enumerable.Range(0, 4).Select(_ => E("z", AgentWorkEventKind.PrMerged))
            .Concat(Enumerable.Range(0, 2).Select(_ => E("z", AgentWorkEventKind.PrReverted)));
        var hints = AgentMetricsCalculator.ForAgent("z", many, _window).CompanyHints;

        Assert.Single(hints);
        Assert.Contains("could help", hints[0]);
    }

    private static AgentAnalyticsService Service(AnalyticsVisibility v) => new(new FixedSource(_events), new AnalyticsOptions(v));

    [Theory]
    [InlineData(AnalyticsVisibility.Owner)]
    [InlineData(AnalyticsVisibility.OwnerAndSelf)]
    [InlineData(AnalyticsVisibility.Peers)]
    public async Task Owner_sees_every_agent_under_every_setting(AnalyticsVisibility v)
    {
        var r = await Service(v).ReadAsync(new AnalyticsViewer("owner", true, false), _window, TestContext.Current.CancellationToken);

        Assert.Equal(["amy", "bob"], r.Agents.Select(a => a.AgentId));
    }

    [Fact]
    public async Task Default_owner_and_self_shows_an_agent_only_itself()
    {
        var r = await Service(AnalyticsVisibility.OwnerAndSelf).ReadAsync(new AnalyticsViewer("amy", false, true), _window, TestContext.Current.CancellationToken);

        Assert.Equal(["amy"], r.Agents.Select(a => a.AgentId));
        Assert.Equal(2, r.Company.TasksCompleted);
    }

    [Fact]
    public async Task Owner_only_hides_agents_from_agents_and_peers_shows_them()
    {
        var hidden = await Service(AnalyticsVisibility.Owner).ReadAsync(new AnalyticsViewer("amy", false, true), _window, TestContext.Current.CancellationToken);
        var peers = await Service(AnalyticsVisibility.Peers).ReadAsync(new AnalyticsViewer("amy", false, true), _window, TestContext.Current.CancellationToken);
        var human = await Service(AnalyticsVisibility.Peers).ReadAsync(new AnalyticsViewer("sam", false, false), _window, TestContext.Current.CancellationToken);

        Assert.Empty(hidden.Agents);
        Assert.Equal(2, peers.Agents.Count);
        Assert.Empty(human.Agents);
    }

    [Fact]
    public async Task Agents_are_ordered_by_id_never_by_a_figure()
    {
        // zed has the best numbers and still comes last.
        var events = _events.Concat([E("zed", AgentWorkEventKind.TaskCompleted), E("zed", AgentWorkEventKind.TaskCompleted), E("zed", AgentWorkEventKind.TaskCompleted)]).ToArray();
        var svc = new AgentAnalyticsService(new FixedSource(events), new AnalyticsOptions(AnalyticsVisibility.Peers));

        var r = await svc.ReadAsync(new AnalyticsViewer("owner", true, false), _window, TestContext.Current.CancellationToken);

        Assert.Equal(["amy", "bob", "zed"], r.Agents.Select(a => a.AgentId));
    }

    [Fact]
    public void Visibility_setting_parses_and_a_bad_value_falls_back_to_the_narrow_default()
    {
        static AnalyticsVisibility Read(string? raw) => AnalyticsComponent.ReadOptions(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Analytics:AgentMetricsVisibility"] = raw }).Build()).AgentMetricsVisibility;

        Assert.Equal(AnalyticsVisibility.Peers, Read("peers"));
        Assert.Equal(AnalyticsVisibility.OwnerAndSelf, Read(null));
        Assert.Equal(AnalyticsVisibility.OwnerAndSelf, Read("everyone"));
        Assert.Equal(AnalyticsVisibility.OwnerAndSelf, Read("99"));
    }

    [Fact]
    public async Task Component_registers_a_working_service_with_no_collectors()
    {
        var services = new ServiceCollection();
        new AnalyticsComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var sp = services.BuildServiceProvider();

        var r = await sp.GetRequiredService<IAgentAnalytics>().ReadAsync(new AnalyticsViewer("owner", true, false), _window, TestContext.Current.CancellationToken);

        Assert.Empty(r.Agents);
        Assert.Null(r.Company.RevertRate);
    }

    [Fact]
    public async Task Composite_source_merges_collectors_and_skips_one_that_throws()
    {
        var composite = new CompositeWorkEventSource([new FixedCollector(_events), new ThrowingCollector(), new FixedCollector([E("zed", AgentWorkEventKind.TaskCompleted)])]);

        var all = await composite.ReadAsync(_window, TestContext.Current.CancellationToken);

        Assert.Equal(_events.Length + 1, all.Count);
    }

    [Fact]
    public async Task Pane_model_carries_nulls_for_gaps()
    {
        var r = await Service(AnalyticsVisibility.Peers).ReadAsync(new AnalyticsViewer("owner", true, false), _window, TestContext.Current.CancellationToken);

        var pane = AnalyticsPaneBuilder.Build(r);

        Assert.Equal(PaneKinds.Analytics, pane.Kind);
        Assert.Equal(JsonValueKind.Number, pane.Model.GetProperty("company").GetProperty("revertRate").ValueKind);
        var bob = pane.Model.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("agentId").GetString() == "bob");
        Assert.Equal(JsonValueKind.Null, bob.GetProperty("reviewPassRate").ValueKind);
    }

    [Fact]
    public void Report_types_have_no_rank_or_score_member()
    {
        var names = typeof(AgentMetrics).GetProperties().Concat(typeof(AnalyticsReport).GetProperties()).Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.Contains("Rank", StringComparison.OrdinalIgnoreCase) || n.Contains("Score", StringComparison.OrdinalIgnoreCase) || n.Contains("Cost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Doc 25 Boundaries: the metrics assemblies cannot reference reputation, scheduling, voting or Soul code.</summary>
    [Theory]
    [InlineData(typeof(AgentMetricsCalculator))]
    [InlineData(typeof(AnalyticsVisibility))]
    public void Analytics_assemblies_reference_no_reputation_schedule_vote_or_soul_code(Type marker)
    {
        string[] forbidden = ["Bazaar.Server.Reputation", "Bazaar.Server.WorkLife", "Bazaar.Server.Council", "Bazaar.Server.Souls", "Bazaar.Server.Memory", "Bazaar.Server"];

        var refs = marker.Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(refs, r => forbidden.Contains(r) || r.StartsWith("Bazaar.Server.Reputation", StringComparison.Ordinal));
    }

    private sealed class FixedCollector(AgentWorkEvent[] events) : IWorkEventCollector
    {
        public Task<IReadOnlyList<AgentWorkEvent>> CollectAsync(AnalyticsWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentWorkEvent>>(events);
    }

    private sealed class ThrowingCollector : IWorkEventCollector
    {
        public Task<IReadOnlyList<AgentWorkEvent>> CollectAsync(AnalyticsWindow window, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
    }

    private sealed class FixedSource(AgentWorkEvent[] events) : IWorkEventSource
    {
        public Task<IReadOnlyList<AgentWorkEvent>> ReadAsync(AnalyticsWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentWorkEvent>>(events);
    }
}
