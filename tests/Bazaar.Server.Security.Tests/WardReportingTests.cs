using System.Text.Json;
using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests;

public class WardReportingTests
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    private sealed class RecordingIntake(bool throws = false) : IWardTriggerIntake
    {
        public List<(Guid Agent, string Limit)> HardStops { get; } = [];

        public List<(Guid Agent, int Count)> Denials { get; } = [];

        public Task<WardOutcome?> ReportOutcomesAsync(Guid agentId, string tag, IReadOnlyList<bool> recent, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken ct)
        {
            Denials.Add((agentId, deniedCount));
            return throws ? throw new InvalidOperationException("ward down") : Task.FromResult<WardOutcome?>(null);
        }

        public Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken ct)
        {
            HardStops.Add((agentId, limitName));
            return throws ? throw new InvalidOperationException("ward down") : Task.FromResult(WardOutcome.Refuse("x", null));
        }

        public Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FixedEngine(PolicyOutcome outcome, PolicyReason reason) : IToolPolicyEngine
    {
        public PolicyDecision Evaluate(DispatchContext context) => new(outcome, reason);
    }

    private static DispatchContext Context(string agentId, DateTimeOffset at) =>
        new(agentId, Guid.NewGuid(), SessionRoles.Actor, "some.tool", null, null, JsonElement.Parse("{}"), SessionTaint.Clean, at);

    private static TokenBudgetService Budget(IWardTriggerIntake? ward) =>
        new(new TokenBudgetOptions { HourlyTokens = 100, DailyTokens = 1000, WeeklyTokens = 5000 }, new InMemoryTokenBudgetStore(), new FakeTimeProvider(_t0), ward);

    [Fact]
    public async Task A_denied_budget_call_is_reported_as_a_hard_stop_after_the_denial()
    {
        var ward = new RecordingIntake();
        using var service = Budget(ward);

        var decision = await service.TryConsume(_agent.ToString(), 500, TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal((_agent, "Hour window"), Assert.Single(ward.HardStops));
    }

    [Fact]
    public async Task An_allowed_call_and_a_non_guid_agent_report_nothing()
    {
        var ward = new RecordingIntake();
        using var service = Budget(ward);

        await service.TryConsume(_agent.ToString(), 10, TestContext.Current.CancellationToken);
        await service.TryConsume("agent-a", 500, TestContext.Current.CancellationToken);

        Assert.Empty(ward.HardStops);
    }

    [Fact]
    public async Task A_failing_report_never_changes_the_budget_decision()
    {
        using var service = Budget(new RecordingIntake(throws: true));

        var decision = await service.TryConsume(_agent.ToString(), 500, TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Without_a_ward_the_budget_works_as_before()
    {
        using var service = Budget(null);

        Assert.False((await service.TryConsume(_agent.ToString(), 500, TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task Denials_are_counted_in_the_window_and_reported_with_the_decision_unchanged()
    {
        var ward = new RecordingIntake();
        var engine = new WardReportingToolPolicyEngine(new FixedEngine(PolicyOutcome.Deny, PolicyReason.TierDenied), ward, new DenialWindowOptions());

        var decisions = new[] { _t0, _t0.AddMinutes(1), _t0.AddMinutes(2) }.Select(t => engine.Evaluate(Context(_agent.ToString(), t))).ToArray();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.All(decisions, d => Assert.Equal(PolicyOutcome.Deny, d.Outcome));
        Assert.Equal([1, 2, 3], ward.Denials.Select(d => d.Count).Order());
    }

    [Fact]
    public async Task Old_denials_leave_the_window()
    {
        var ward = new RecordingIntake();
        var engine = new WardReportingToolPolicyEngine(new FixedEngine(PolicyOutcome.Deny, PolicyReason.TierDenied), ward, new DenialWindowOptions { Window = TimeSpan.FromMinutes(10) });

        engine.Evaluate(Context(_agent.ToString(), _t0));
        engine.Evaluate(Context(_agent.ToString(), _t0.AddMinutes(30)));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([1, 1], ward.Denials.Select(d => d.Count));
    }

    [Theory]
    [InlineData(PolicyOutcome.Allow, PolicyReason.Autonomous)]
    [InlineData(PolicyOutcome.Deny, PolicyReason.RateLimited)]
    [InlineData(PolicyOutcome.RequireApproval, PolicyReason.HardTaintTier2)]
    public async Task Only_real_denials_count(PolicyOutcome outcome, PolicyReason reason)
    {
        var ward = new RecordingIntake();
        var engine = new WardReportingToolPolicyEngine(new FixedEngine(outcome, reason), ward, new DenialWindowOptions());

        engine.Evaluate(Context(_agent.ToString(), _t0));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(ward.Denials);
    }

    [Fact]
    public async Task A_failing_report_does_not_break_the_policy_decision()
    {
        var engine = new WardReportingToolPolicyEngine(new FixedEngine(PolicyOutcome.Deny, PolicyReason.TierDenied), new RecordingIntake(throws: true), new DenialWindowOptions());

        var decision = engine.Evaluate(Context(_agent.ToString(), _t0));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }
}

public class WardReportingRegistrationTests
{
    private sealed class AllowAll : IToolPolicyEngine
    {
        public PolicyDecision Evaluate(DispatchContext context) => new(PolicyOutcome.Allow, PolicyReason.Autonomous);
    }

    [Fact]
    public void The_registered_engine_is_wrapped_and_a_host_without_one_is_left_alone()
    {
        var with = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        with.AddSingleton<IToolPolicyEngine, AllowAll>();
        with.AddSingleton<IWardTriggerIntake>(new WardReportingTests_NoIntake());
        with.AddWardReportingForToolPolicy();
        using var sp = with.BuildServiceProvider();

        Assert.IsType<WardReportingToolPolicyEngine>(sp.GetRequiredService<IToolPolicyEngine>());

        var without = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        without.AddWardReportingForToolPolicy();
        Assert.DoesNotContain(without, d => d.ServiceType == typeof(IToolPolicyEngine));
    }
}

internal sealed class WardReportingTests_NoIntake : IWardTriggerIntake
{
    public Task<WardOutcome?> ReportOutcomesAsync(Guid agentId, string tag, IReadOnlyList<bool> recent, CancellationToken ct) => throw new NotSupportedException();

    public Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken ct) => throw new NotSupportedException();

    public Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken ct) => throw new NotSupportedException();

    public Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken ct) => throw new NotSupportedException();

    public Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();

    public Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken ct) => throw new NotSupportedException();
}
