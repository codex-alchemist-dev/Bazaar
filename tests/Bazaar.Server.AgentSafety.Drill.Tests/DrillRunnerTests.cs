using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Drill.Tests;

public sealed class DrillRunnerTests
{
    private static readonly string[] _cleanSteps = ["baseline", "engage", "stop-visible", "containment", "release", "released"];

    [Fact]
    public async Task A_clean_drill_passes_every_step_and_releases_the_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.True(result.Passed);
        Assert.Equal(_cleanSteps, result.Steps.Select(s => s.Name));
        Assert.All(result.Steps, s => Assert.True(s.Passed));
        Assert.Empty(h.Engine.GetStatus().AgentStops);
        Assert.Empty(h.Containment.ContainedAgents);
        Assert.Contains(h.Audit.Entries, e => e.Action == KillSwitchAction.Resumed && e.ActorId == FakeDrillTarget.DeviceId);
    }

    [Fact]
    public async Task A_failing_containment_step_is_named_and_the_agent_is_still_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        h.Containment.SkipEgressBlock = true;

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.False(result.Passed);
        var failed = Assert.Single(result.Steps, s => !s.Passed && s.Name != "cleanup");
        Assert.Equal("containment", failed.Name);
        Assert.Equal("Egress was not blocked.", failed.Reason);
        Assert.DoesNotContain(result.Steps, s => s.Name == "release");
        Assert.True(Assert.Single(result.Steps, s => s.Name == "cleanup").Passed);
        Assert.Empty(h.Engine.GetStatus().AgentStops);
    }

    [Fact]
    public async Task A_throwing_probe_fails_its_step_without_leaking_the_message_and_releases_the_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        h.Containment.ThrowWhileContained = true;

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.False(result.Passed);
        var failed = Assert.Single(result.Steps, s => !s.Passed && s.Name == "containment");
        Assert.Equal("The step threw InvalidOperationException.", failed.Reason);
        Assert.DoesNotContain(result.Steps, s => s.Reason.Contains("must not reach", StringComparison.Ordinal));
        Assert.True(Assert.Single(result.Steps, s => s.Name == "cleanup").Passed);
        Assert.Empty(h.Engine.GetStatus().AgentStops);
        Assert.Empty(h.Containment.ContainedAgents);
    }

    [Fact]
    public async Task A_cancelled_drill_releases_the_agent_before_the_cancellation_reaches_the_caller()
    {
        var outer = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        h.Containment.OnObserved = observed =>
        {
            if (observed.IsContained)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runner.RunAsync(FakeDrillTarget.AgentId, cts.Token));

        Assert.Empty(h.Engine.GetStatus().AgentStops);
        Assert.Empty(h.Containment.ContainedAgents);
        Assert.Contains(h.Audit.Entries, e => e.Action == KillSwitchAction.Resumed && e.AgentId == FakeDrillTarget.AgentId);
    }

    [Fact]
    public async Task A_drill_refuses_an_agent_that_is_not_marked_as_a_drill_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Runner.RunAsync("agent-7", ct));

        Assert.Empty(h.Engine.GetStatus().AgentStops);
        Assert.Empty(h.Audit.Entries);
        Assert.Empty(h.Containment.ContainedAgents);
    }

    [Fact]
    public async Task A_drill_does_not_run_while_a_global_stop_is_engaged()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        await h.Engine.EngageGlobalAsync("owner-device", "incident", ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.RunAsync(FakeDrillTarget.AgentId, ct));

        Assert.Empty(h.Engine.GetStatus().AgentStops);
        Assert.NotNull(h.Engine.GetStatus().Global);
    }

    [Fact]
    public async Task A_stop_left_by_an_earlier_run_fails_the_baseline_and_is_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        await h.Engine.EngageAgentAsync(DrillRunner.DrillActorId, FakeDrillTarget.AgentId, "left over", ct);

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.False(result.Passed);
        var baseline = Assert.Single(result.Steps, s => s.Name == "baseline");
        Assert.False(baseline.Passed);
        Assert.True(Assert.Single(result.Steps, s => s.Name == "cleanup").Passed);
        Assert.Empty(h.Engine.GetStatus().AgentStops);
    }

    [Fact]
    public async Task A_denied_release_is_reported_and_the_result_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        h.Containment.DenyRelease = true;

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.False(result.Passed);
        var release = Assert.Single(result.Steps, s => s.Name == "release");
        Assert.Equal("The step threw UnauthorizedAccessException.", release.Reason);
        var cleanup = Assert.Single(result.Steps, s => s.Name == "cleanup");
        Assert.False(cleanup.Passed);
        // The containment refused the release twice, so the stop stays. The result says so instead of hiding it.
        Assert.Single(h.Engine.GetStatus().AgentStops);
    }

    [Fact]
    public async Task A_missing_release_challenge_fails_the_release_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        h.Containment.IssueNoChallenge = true;

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        var release = Assert.Single(result.Steps, s => s.Name == "release");
        Assert.False(release.Passed);
        Assert.Equal("No release challenge was issued to the drill device.", release.Reason);
    }

    [Fact]
    public async Task A_stop_the_drill_did_not_engage_is_neither_signed_for_nor_released_by_the_cleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        await h.Engine.EngageAgentAsync("owner-1", FakeDrillTarget.AgentId, "a real stop", ct);

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.False(result.Passed);
        Assert.DoesNotContain(result.Steps, s => s.Name == "cleanup");
        Assert.Equal(0, h.Target.SignCalls);
        var stop = Assert.Single(h.Engine.GetStatus().AgentStops);
        Assert.Equal("owner-1", stop.ActorId);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Action == KillSwitchAction.Resumed);
    }

    [Fact]
    public async Task The_drill_key_does_not_sign_once_the_agent_is_no_longer_a_drill_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = new DrillHarness();
        h.Target.RejectAfterChecks = 1; // accepted at the start of the run, rejected at every later check

        var result = await h.Runner.RunAsync(FakeDrillTarget.AgentId, ct);

        Assert.False(result.Passed);
        Assert.Equal(0, h.Target.SignCalls);
        Assert.Contains(result.Steps, s => s.Name == "release" && !s.Passed);
        Assert.Single(h.Engine.GetStatus().AgentStops);
    }
}
