using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Tests;

public class BrowserPoolServiceTests
{
    private static async Task<BrowserPoolException> Refused(Task<PoolSession> open)
    {
        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => open);
        return ex;
    }

    [Fact]
    public async Task Open_runs_prepare_then_firewall_then_start_in_that_order()
    {
        using var rig = new Rig();

        var session = await rig.OpenAsync("agent-1");

        Assert.Equal(["run:rm", "run:install", "run:install", "run:install", "fw", "start"], rig.Host.Log);
        Assert.Equal(0, session.Slot);
        Assert.Equal("agent-1", session.AgentId);
        Assert.Equal(32, session.SessionId.Length);
        Assert.Equal(rig.Time.GetUtcNow(), session.StartedAt);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromHours(2), session.ExpiresAt);
    }

    [Fact]
    public async Task Firewall_rule_for_the_session_is_in_force_before_start()
    {
        using var rig = new Rig();

        await rig.OpenAsync("agent-1");

        Assert.Equal([new PoolFirewallRule(20000, "10.0.0.1", 3128)], rig.Host.Firewalls[0]);
        Assert.Equal(20000, rig.Host.Started[0].Uid);
    }

    [Fact]
    public async Task Session_ids_are_random_and_distinct()
    {
        using var rig = new Rig();

        var ids = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add((await rig.OpenAsync("a" + i)).SessionId);
        }

        Assert.Equal(10, ids.Count);
        Assert.All(ids, id => Assert.True(Guid.TryParseExact(id, "N", out _)));
    }

    [Fact]
    public async Task Slots_are_allocated_lowest_first_and_reused_after_cleanup()
    {
        using var rig = new Rig();
        var a = await rig.OpenAsync("a");
        var b = await rig.OpenAsync("b");
        var c = await rig.OpenAsync("c");

        await rig.Pool.CloseAsync(b.SessionId, TestContext.Current.CancellationToken);
        var d = await rig.OpenAsync("d");

        Assert.Equal([0, 1, 2], new[] { a.Slot, b.Slot, c.Slot });
        Assert.Equal(1, d.Slot);
    }

    [Fact]
    public async Task Close_wipes_the_slot_and_removes_the_firewall_rule()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Equal(2, rig.Host.Count("rm"));
        Assert.Single(rig.Host.Stopped);
        Assert.Empty(rig.Host.Firewalls[^1]);
        Assert.Empty(rig.Pool.Sessions("a"));
    }

    [Fact]
    public async Task Close_is_idempotent_and_ignores_unknown_ids()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);
        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);
        await rig.Pool.CloseAsync("nope", TestContext.Current.CancellationToken);
        await rig.Pool.CloseAsync(null!, TestContext.Current.CancellationToken);

        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Single(rig.Audit.ClosedRows);
    }

    [Fact]
    public async Task Concurrent_closes_run_cleanup_once()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken)));

        Assert.Equal(1, rig.Host.Count("pkill"));
    }

    [Fact]
    public async Task Sessions_lists_only_the_agents_open_sessions()
    {
        using var rig = new Rig();
        await rig.OpenAsync("a");
        await rig.OpenAsync("a");
        await rig.OpenAsync("b");

        Assert.Equal(2, rig.Pool.Sessions("a").Count);
        Assert.Single(rig.Pool.Sessions("b"));
        Assert.Empty(rig.Pool.Sessions("c"));
    }

    [Fact]
    public async Task Headed_session_reports_its_display_mode()
    {
        using var rig = new Rig();

        var s = await rig.OpenAsync("a", PoolDisplayMode.Headed);

        Assert.Equal(PoolDisplayMode.Headed, s.Display);
        Assert.Contains("--ozone-platform=wayland", rig.Host.Started[0].Launch);
    }

    [Fact]
    public async Task Disabled_pool_refuses_everything()
    {
        using var rig = new Rig(o => o.Enabled = false);

        Assert.Equal(PoolFailure.Disabled, (await Refused(rig.OpenAsync("a"))).Failure);
        Assert.Empty(rig.Host.Log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad agent")]
    [InlineData("-x")]
    public async Task Invalid_agent_is_refused_without_touching_the_host(string agent)
    {
        using var rig = new Rig();

        Assert.Equal(PoolFailure.InvalidRequest, (await Refused(rig.OpenAsync(agent))).Failure);
        Assert.Empty(rig.Host.Log);
        Assert.Equal("invalid", rig.Audit.RefusedRows[0].Agent);
    }

    [Theory]
    [InlineData("--no-sandbox")]
    [InlineData("ftp://example.com/")]
    [InlineData("http://someone@example.com/")]
    public async Task Invalid_url_is_refused(string url)
    {
        using var rig = new Rig();

        Assert.Equal(PoolFailure.InvalidRequest, (await Refused(rig.OpenAsync("a", url: url))).Failure);
        Assert.Empty(rig.Host.Log);
    }

    [Fact]
    public async Task Null_request_is_refused()
    {
        using var rig = new Rig();

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => rig.Pool.OpenAsync(null!, TestContext.Current.CancellationToken));

        Assert.Equal(PoolFailure.InvalidRequest, ex.Failure);
    }

    [Fact]
    public async Task Blocked_agent_is_refused_and_audited()
    {
        using var rig = new Rig();
        rig.Gate.Blocked.Add("bad");

        var ex = await Refused(rig.OpenAsync("bad"));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Equal(("bad", PoolFailure.AgentBlocked), (rig.Audit.RefusedRows[0].Agent, rig.Audit.RefusedRows[0].Failure));
        Assert.Empty(rig.Host.Log);
    }

    [Fact]
    public async Task Gate_closing_during_prepare_stops_the_open_and_cleans_up()
    {
        using var rig = new Rig();
        rig.Host.OnRun = (c, n, _) =>
        {
            if (n == 1)
            {
                rig.Gate.Blocked.Add("a");
            }

            return Task.CompletedTask;
        };

        var ex = await Refused(rig.OpenAsync("a"));

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Empty(rig.Host.Started);
        Assert.Equal(1, rig.Host.Count("pkill"));
    }

    [Fact]
    public async Task Agent_without_an_egress_proxy_gets_no_browser()
    {
        using var rig = new Rig();
        rig.Egress.Resolver = _ => Task.FromResult<PoolEgressEndpoint?>(null);

        Assert.Equal(PoolFailure.NoEgress, (await Refused(rig.OpenAsync("a"))).Failure);
        Assert.Empty(rig.Host.Log);
    }

    [Theory]
    [InlineData("proxy.example.com")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public async Task Hostname_egress_endpoint_is_refused_as_no_egress(string host)
    {
        using var rig = new Rig();
        rig.Egress.Resolver = _ => Task.FromResult<PoolEgressEndpoint?>(new PoolEgressEndpoint(host, 3128));

        Assert.Equal(PoolFailure.NoEgress, (await Refused(rig.OpenAsync("a"))).Failure);
    }

    [Fact]
    public async Task Failing_egress_policy_fails_closed()
    {
        using var rig = new Rig();
        rig.Egress.Resolver = _ => throw new InvalidOperationException("boom");

        Assert.Equal(PoolFailure.NoEgress, (await Refused(rig.OpenAsync("a"))).Failure);
    }

    [Fact]
    public async Task Per_agent_limit_counts_opening_and_open_sessions()
    {
        using var rig = new Rig(o => o.MaxSessionsPerAgent = 2);
        await rig.OpenAsync("a");
        await rig.OpenAsync("a");

        Assert.Equal(PoolFailure.AgentLimit, (await Refused(rig.OpenAsync("a"))).Failure);
        await rig.OpenAsync("b");
    }

    [Fact]
    public async Task Pool_limit_is_enforced_and_frees_on_close()
    {
        using var rig = new Rig(o => { o.MaxSessions = 2; o.MaxSessionsPerAgent = 2; });
        var a = await rig.OpenAsync("a");
        await rig.OpenAsync("b");

        Assert.Equal(PoolFailure.PoolFull, (await Refused(rig.OpenAsync("c"))).Failure);
        await rig.Pool.CloseAsync(a.SessionId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await rig.OpenAsync("c")).Slot);
    }

    [Fact]
    public async Task Concurrent_opens_never_exceed_the_limits_or_share_a_slot()
    {
        using var rig = new Rig(o => { o.MaxSessions = 8; o.MaxSessionsPerAgent = 8; });

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            try
            {
                return (PoolSession?)await rig.OpenAsync("agent" + (i % 5));
            }
            catch (BrowserPoolException)
            {
                return null;
            }
        }));

        var opened = results.Where(r => r is not null).Select(r => r!).ToList();
        Assert.Equal(8, opened.Count);
        Assert.Equal(8, opened.Select(s => s.Slot).Distinct().Count());
    }

    [Fact]
    public async Task Failed_prepare_runs_cleanup_and_frees_the_slot()
    {
        using var rig = new Rig();
        rig.Host.FailRun = c => c.Argv[0] == "install";

        var ex = await Refused(rig.OpenAsync("a"));

        Assert.Equal(PoolFailure.LaunchFailed, ex.Failure);
        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Empty(rig.Host.Started);
        rig.Host.FailRun = null;
        Assert.Equal(0, (await rig.OpenAsync("a")).Slot);
    }

    [Fact]
    public async Task Failed_start_runs_cleanup_and_frees_the_slot()
    {
        using var rig = new Rig();
        rig.Host.OnStart = (_, _) => throw new InvalidOperationException("no chromium");

        var ex = await Refused(rig.OpenAsync("a"));

        Assert.Equal(PoolFailure.LaunchFailed, ex.Failure);
        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Empty(rig.Pool.Snapshot().Sessions);
        Assert.Equal(PoolFailure.LaunchFailed, rig.Audit.RefusedRows[^1].Failure);
        Assert.Empty(rig.Audit.OpenedRows);
        Assert.Empty(rig.Audit.ClosedRows);
    }

    [Fact]
    public async Task Failure_message_has_no_inner_message_text()
    {
        using var rig = new Rig();
        rig.Host.OnStart = (_, _) => throw new InvalidOperationException("page content here");

        var ex = await Refused(rig.OpenAsync("a"));

        Assert.DoesNotContain("page content", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_and_cleans_up()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.Host.OnRun = async (_, n, ct) =>
        {
            if (n == 1)
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Pool.OpenAsync(new PoolSessionRequest("a"), cts.Token));

        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Empty(rig.Pool.Snapshot().Quarantined);
    }

    [Fact]
    public async Task Failed_cleanup_quarantines_the_slot_and_audits()
    {
        using var rig = new Rig(o => { o.MaxSessions = 2; o.MaxSessionsPerAgent = 2; });
        var s = await rig.OpenAsync("a");
        rig.Host.FailRun = c => c.Argv[0] == "pkill";

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        var snap = rig.Pool.Snapshot();
        Assert.Equal(0, Assert.Single(snap.Quarantined).Slot);
        Assert.Equal(s.SessionId, snap.Quarantined[0].SessionId);
        Assert.Single(rig.Audit.QuarantineRows);
    }

    [Fact]
    public async Task Cleanup_runs_every_command_even_when_the_first_fails()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");
        var rmBefore = rig.Host.Count("rm");
        rig.Host.FailRun = c => c.Argv[0] == "pkill";

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(rmBefore + 1, rig.Host.Count("rm"));
    }

    [Fact]
    public async Task Quarantined_slot_counts_against_capacity_and_is_not_reused()
    {
        using var rig = new Rig(o => { o.MaxSessions = 2; o.MaxSessionsPerAgent = 2; });
        var s = await rig.OpenAsync("a");
        rig.Host.FailRun = c => c.Argv[0] == "pkill";
        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);
        rig.Host.FailRun = null;

        var next = await rig.OpenAsync("b");
        var full = await Refused(rig.OpenAsync("c"));

        Assert.Equal(1, next.Slot);
        Assert.Equal(PoolFailure.PoolFull, full.Failure);
    }

    [Fact]
    public async Task Sweep_retries_quarantined_slots_and_frees_them_on_success()
    {
        using var rig = new Rig(o => { o.MaxSessions = 1; o.MaxSessionsPerAgent = 1; });
        var s = await rig.OpenAsync("a");
        rig.Host.FailRun = c => c.Argv[0] == "pkill";
        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        await rig.Pool.SweepAsync(TestContext.Current.CancellationToken);
        Assert.Single(rig.Pool.Snapshot().Quarantined);

        rig.Host.FailRun = null;
        await rig.Pool.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Empty(rig.Pool.Snapshot().Quarantined);
        Assert.Equal(0, (await rig.OpenAsync("a")).Slot);
    }

    [Fact]
    public async Task Quarantined_slot_has_no_firewall_accept_rule()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");
        rig.Host.FailRun = c => c.Argv[0] == "pkill";

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Empty(rig.Pool.Snapshot().FirewallRules);
        Assert.Empty(rig.Host.Firewalls[^1]);
    }

    [Fact]
    public async Task Kill_switch_closes_all_of_the_agents_sessions_only()
    {
        using var rig = new Rig();
        await rig.OpenAsync("a");
        await rig.OpenAsync("a");
        var other = await rig.OpenAsync("b");

        var n = await rig.Pool.CloseAllForAgentAsync("a", TestContext.Current.CancellationToken);

        Assert.Equal(2, n);
        Assert.Empty(rig.Pool.Sessions("a"));
        Assert.Single(rig.Pool.Sessions("b"));
        Assert.All(rig.Audit.ClosedRows, r => Assert.Equal(PoolCloseReason.KillSwitch, r.Reason));
        Assert.Equal(other.SessionId, rig.Pool.Sessions("b")[0].SessionId);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("bad agent")]
    [InlineData("")]
    public async Task Kill_switch_for_an_agent_without_sessions_returns_zero(string agent)
    {
        using var rig = new Rig();

        Assert.Equal(0, await rig.Pool.CloseAllForAgentAsync(agent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kill_switch_does_not_wait_for_a_slow_open_and_the_open_ends_cleaned_up()
    {
        using var rig = new Rig(o => { o.MaxSessions = 2; o.MaxSessionsPerAgent = 2; });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.OnRun = async (_, n, _) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await release.Task; // deliberately ignores cancellation, like a stuck guest channel
            }
        };

        var open = rig.OpenAsync("a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var closed = await rig.Pool.CloseAllForAgentAsync("a", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, closed);
        Assert.False(open.IsCompleted);
        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Empty(rig.Pool.Sessions("a"));

        // The slot is still held while the stuck open is alive, so another agent cannot be handed its directory.
        Assert.Equal(1, (await rig.OpenAsync("b")).Slot);

        release.SetResult();
        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => open);

        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.DoesNotContain(rig.Host.Started, s => s.AgentId == "a");
        Assert.Equal(2, rig.Host.Count("pkill"));
        Assert.Empty(rig.Pool.Snapshot().Quarantined);
        Assert.Equal(0, (await rig.OpenAsync("a")).Slot);
    }

    [Fact]
    public async Task Kill_switch_cancels_an_open_whose_host_honours_cancellation()
    {
        using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.OnRun = async (_, n, ct) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        var open = rig.OpenAsync("a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await rig.Pool.CloseAllForAgentAsync("a", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => open.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(PoolFailure.AgentBlocked, ex.Failure);
        Assert.Empty(rig.Pool.Snapshot().Quarantined);
    }

    [Fact]
    public async Task Close_during_start_stops_the_browser_that_appears_and_ends_the_open()
    {
        using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.OnStart = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        var open = rig.OpenAsync("a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await rig.Pool.CloseAllForAgentAsync("a", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.SetResult();

        await Assert.ThrowsAsync<BrowserPoolException>(() => open);

        Assert.Single(rig.Host.Stopped);
        Assert.Equal(2, rig.Host.Count("pkill"));
        Assert.Empty(rig.Pool.Snapshot().Sessions);
        Assert.Empty(rig.Audit.OpenedRows);
    }

    [Fact]
    public async Task Close_by_session_id_cancels_an_open_in_flight()
    {
        using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.OnRun = async (_, n, ct) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        var open = rig.OpenAsync("a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var id = rig.Pool.Snapshot();
        Assert.Equal(1, id.Opening);

        await rig.Pool.CloseAllForAgentAsync("a", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<BrowserPoolException>(() => open);
    }

    [Fact]
    public async Task Sweep_closes_idle_sessions_after_the_idle_timeout()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        rig.Time.Advance(TimeSpan.FromMinutes(19));
        Assert.Equal(0, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));

        Assert.Empty(rig.Pool.Sessions("a"));
        Assert.Equal((s.SessionId, PoolCloseReason.IdleTimeout), (rig.Audit.ClosedRows[0].Session.SessionId, rig.Audit.ClosedRows[0].Reason));
    }

    [Fact]
    public async Task Touch_restarts_the_idle_clock()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        rig.Time.Advance(TimeSpan.FromMinutes(15));
        rig.Pool.Touch(s.SessionId);
        rig.Time.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(0, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Touch_cannot_extend_past_the_hard_lifetime()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        for (var i = 0; i < 11; i++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(10));
            rig.Pool.Touch(s.SessionId);
        }

        Assert.Equal(0, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        rig.Pool.Touch(s.SessionId);

        Assert.Equal(1, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PoolCloseReason.Expired, rig.Audit.ClosedRows[0].Reason);
    }

    [Fact]
    public void Touch_of_unknown_ids_is_ignored()
    {
        using var rig = new Rig();

        rig.Pool.Touch("nope");
        rig.Pool.Touch(null!);
    }

    [Fact]
    public async Task Sweep_leaves_active_sessions_alone()
    {
        using var rig = new Rig();
        await rig.OpenAsync("a");

        Assert.Equal(0, await rig.Pool.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Single(rig.Pool.Sessions("a"));
    }

    [Fact]
    public async Task Sweep_abandons_an_open_that_has_hung_past_the_open_timeout()
    {
        using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Host.OnRun = async (_, n, ct) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var open = rig.OpenAsync("a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        rig.Time.Advance(TimeSpan.FromMinutes(3));
        var closed = await rig.Pool.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, closed);
        await Assert.ThrowsAsync<BrowserPoolException>(() => open.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(2, rig.Host.Count("pkill")); // once at teardown, once more after the stuck open ended
    }

    [Fact]
    public async Task Snapshot_reports_capacity_sessions_and_rules()
    {
        using var rig = new Rig();
        var a = await rig.OpenAsync("a");
        var b = await rig.OpenAsync("b");

        var snap = rig.Pool.Snapshot();

        Assert.Equal(32, snap.Capacity);
        Assert.Equal(2, snap.Open);
        Assert.Equal(0, snap.Opening);
        Assert.Equal([a.SessionId, b.SessionId], snap.Sessions.Select(s => s.SessionId));
        Assert.Equal(2, snap.FirewallRules.Count);
        Assert.Equal([20000, 20001], snap.FirewallRules.Select(r => r.Uid));
    }

    [Fact]
    public async Task Audit_records_open_and_close_with_reason()
    {
        using var rig = new Rig();
        var s = await rig.OpenAsync("a");

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(s, Assert.Single(rig.Audit.OpenedRows));
        Assert.Equal(PoolCloseReason.Requested, Assert.Single(rig.Audit.ClosedRows).Reason);
    }

    [Fact]
    public async Task A_throwing_audit_sink_does_not_break_open_or_close()
    {
        var host = new FakeHost();
        using var pool = new BrowserPoolService(
            Microsoft.Extensions.Options.Options.Create(new BrowserPoolOptions()),
            host,
            new FakeEgress(),
            new FakeGate(),
            new ThrowingAudit(),
            TimeProvider.System);

        var s = await pool.OpenAsync(new PoolSessionRequest("a"), TestContext.Current.CancellationToken);
        await pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(1, host.Count("pkill"));
    }

    [Fact]
    public async Task Different_agents_never_share_a_slot_directory_in_their_commands()
    {
        using var rig = new Rig();
        await rig.OpenAsync("a");
        await rig.OpenAsync("b");

        var dirs = rig.Host.Started.Select(s => s.Prepare[0].Argv[^1]).ToList();

        Assert.Equal(2, dirs.Distinct().Count());
    }

    [Fact]
    public async Task Open_requests_never_carry_anything_but_the_validated_fields_into_the_launch()
    {
        using var rig = new Rig();

        await rig.OpenAsync("agent-1", url: "https://example.com/?a=b");

        var launch = rig.Host.Started[0].Launch;
        Assert.Equal("https://example.com/?a=b", launch[^1]);
        Assert.DoesNotContain(launch, a => a.Contains("agent-1", StringComparison.Ordinal));
    }

    private sealed class ThrowingAudit : IPoolAuditSink
    {
        public void Opened(PoolSession session) => throw new InvalidOperationException();

        public void Closed(PoolSession session, PoolCloseReason reason) => throw new InvalidOperationException();

        public void Refused(string agentId, PoolFailure failure, string detail) => throw new InvalidOperationException();

        public void SlotQuarantined(int slot, string sessionId, string agentId, string detail) => throw new InvalidOperationException();
    }
}
