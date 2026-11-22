using Bazaar.Community.Actions;
using Bazaar.Community.Drafts;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Community.Tests.Drafts;

public sealed class CommunityDraftExpirySweeperTests
{
    private static CommunityDraftExpirySweeper NewSweeper(DraftFixture f, TimeProvider clock) =>
        new(f.Store, new CommunityActionStore(f.Sql, f.Audit), clock);

    [Fact]
    public async Task Expires_a_due_draft_and_writes_one_bodyless_expired_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new DraftFixture();
        var clock = new FakeTimeProvider(f.Draft.ApprovalExpiresAt);

        var count = await NewSweeper(f, clock).SweepAsync(ct);

        Assert.Equal(1, count);
        Assert.Equal(CommunityDraftStatus.Expired, f.Store.All.Single().Status);
        var row = Assert.Single(f.Sql.Rows);
        Assert.Equal("expired", row.Status);
        Assert.Null(row.Body);
        Assert.Null(row.ExternalId);
        Assert.Equal(f.Draft.AgentId, row.AgentId);
        Assert.Equal(f.Draft.ApprovalId.ToString("D"), row.ApprovalId);
        Assert.Equal(f.Draft.Post.Target, row.TargetUrl);
        Assert.Single(f.Audit.Entries);
    }

    [Fact]
    public async Task Leaves_open_and_already_decided_drafts_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new DraftFixture();
        var denied = f.NewDraft(gatesPass: true) with { Status = CommunityDraftStatus.Denied };
        f.Store.Add(denied);
        var clock = new FakeTimeProvider(f.Draft.ApprovalExpiresAt.AddMinutes(-1));

        Assert.Equal(0, await NewSweeper(f, clock).SweepAsync(ct));

        Assert.Equal(CommunityDraftStatus.Pending, (await f.Store.GetAsync(f.Draft.DraftId, ct))!.Status);
        Assert.Equal(CommunityDraftStatus.Denied, (await f.Store.GetAsync(denied.DraftId, ct))!.Status);
        Assert.Empty(f.Sql.Rows);
    }

    [Fact]
    public async Task A_second_sweep_finds_nothing_so_each_draft_is_logged_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new DraftFixture();
        var sweeper = NewSweeper(f, new FakeTimeProvider(f.Draft.ApprovalExpiresAt.AddHours(1)));

        Assert.Equal(1, await sweeper.SweepAsync(ct));
        Assert.Equal(0, await sweeper.SweepAsync(ct));

        Assert.Single(f.Sql.Rows);
    }

    [Fact]
    public async Task A_log_failure_for_one_draft_does_not_stop_the_others()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new DraftFixture();
        f.Store.Add(f.NewDraft(gatesPass: true));
        f.Audit.FailNext = true;
        var sweeper = NewSweeper(f, new FakeTimeProvider(f.Draft.ApprovalExpiresAt.AddHours(1)));

        Assert.Equal(2, await sweeper.SweepAsync(ct));

        Assert.All(f.Store.All, d => Assert.Equal(CommunityDraftStatus.Expired, d.Status));
        Assert.Equal(2, f.Sql.Rows.Count);
        Assert.Single(f.Audit.Entries);
    }

    [Fact]
    public async Task A_decision_after_the_sweep_sees_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new DraftFixture();
        f.Clock.Now = f.Draft.ApprovalExpiresAt.AddMinutes(1);
        await NewSweeper(f, f.Clock).SweepAsync(ct);

        var result = await f.Handler.HandleAsync(f.Draft.DraftId, f.Sign(f.Draft, Bazaar.Contracts.Approvals.ApprovalDecisionKind.Deny), ct);

        Assert.Equal(DraftDecisionStatus.Conflict, result.Status);
        Assert.Equal("expired", result.Code);
    }

    [Fact]
    public void Sweep_interval_defaults_to_five_minutes_and_has_a_one_minute_floor()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new CommunityDraftOptions().SweepInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), new CommunityDraftOptions(ExpirySweepInterval: TimeSpan.FromSeconds(1)).SweepInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), new CommunityDraftOptions(ExpirySweepInterval: TimeSpan.Zero).SweepInterval);
        Assert.Equal(TimeSpan.FromMinutes(7), new CommunityDraftOptions(ExpirySweepInterval: TimeSpan.FromMinutes(7)).SweepInterval);
    }

    private sealed class CountingSweeper : ICommunityDraftExpirySweeper
    {
        public int Calls;
        public int FailFirst;

        public Task<int> SweepAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref Calls);
            return n <= FailFirst ? throw new InvalidOperationException("db down") : Task.FromResult(0);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task The_service_sweeps_each_interval_survives_failures_and_stops_on_cancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var sweeper = new CountingSweeper { FailFirst = 1 };
        using var service = new CommunityDraftExpiryService(() => sweeper, new CommunityDraftOptions(), clock);

        await service.StartAsync(ct);
        await WaitForAsync(() => sweeper.Calls == 1);
        clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(50, ct);
        Assert.Equal(1, sweeper.Calls);

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => sweeper.Calls == 2);
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => sweeper.Calls == 3);

        await service.StopAsync(ct);
        var after = sweeper.Calls;
        clock.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(50, ct);
        Assert.Equal(after, sweeper.Calls);
    }

    [Fact]
    public async Task The_service_survives_a_sweeper_that_cannot_be_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var attempts = 0;
        using var service = new CommunityDraftExpiryService(
            () => Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("no data source") : new CountingSweeper(),
            new CommunityDraftOptions(),
            clock);

        await service.StartAsync(ct);
        await WaitForAsync(() => attempts == 1);
        clock.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => attempts == 2);
        await service.StopAsync(ct);
    }
}
