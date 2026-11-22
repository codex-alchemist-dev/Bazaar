using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Hive.Approvals.Tests;

public sealed class HiveGrantApprovalsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HiveGrantChange _set = new(HiveGrantAction.Set, "proj-a", "proj-b", HiveKind.Procedure);

    private sealed class Rig
    {
        public InMemoryApprovalStore Store { get; } = new();

        public InMemoryHiveGrantChangeLedger Ledger { get; } = new();

        public InMemoryHiveGrantStore Grants { get; } = new();

        public OwnerQueueHiveGrantApprovals Approvals { get; }

        public ApprovalService Service { get; }

        public HiveGrantApprovalSweeper Sweeper { get; }

        public Rig()
        {
            Approvals = new OwnerQueueHiveGrantApprovals(new OwnerApprovalEnqueuer(Store, new InMemoryRateBucketStore()), Ledger);
            Service = new ApprovalService(Store, new InMemoryOwnerKeyRegistry(), new InMemoryRateBucketStore(), new ApprovalOptions());
            var services = new ServiceCollection()
                .AddSingleton<IApprovalStore>(Store)
                .AddSingleton(Service)
                .AddSingleton(new HiveGrantApplier(Grants, null!))
                .BuildServiceProvider();
            Sweeper = new HiveGrantApprovalSweeper(Ledger, services, TimeProvider.System, NullLogger<HiveGrantApprovalSweeper>.Instance);
        }

        public async Task Decide(Guid id, ApprovalStatus status)
        {
            var row = (await Store.GetAsync(id, Ct))!;
            Assert.True(await Store.TryReplaceAsync(id, ApprovalStatus.Pending, row with { Status = status, DecidedBy = "owner-1" }, Ct));
        }
    }

    [Fact]
    public async Task Queues_one_card_per_change_and_repeat_returns_the_same_ticket()
    {
        var rig = new Rig();
        var first = await rig.Approvals.RequestAsync(_set, "owner-1", Ct);
        var again = await rig.Approvals.RequestAsync(_set, "owner-1", Ct);

        Assert.True(first.Accepted);
        Assert.Equal(first.TicketId, again.TicketId);
        var row = await rig.Store.GetAsync(first.TicketId!.Value, Ct);
        Assert.Equal(OwnerQueueHiveGrantApprovals.ToolName, row!.Tool);
        Assert.Single(rig.Ledger.Snapshot());
    }

    [Fact]
    public async Task Different_changes_get_different_keys()
    {
        var revoke = _set with { Action = HiveGrantAction.Revoke };
        Assert.NotEqual(OwnerQueueHiveGrantApprovals.ActionKey(_set), OwnerQueueHiveGrantApprovals.ActionKey(revoke));
        Assert.True(OwnerQueueHiveGrantApprovals.ActionKey(_set).Length <= 128);
    }

    [Fact]
    public async Task Blank_requester_is_refused()
    {
        var rig = new Rig();
        var ticket = await rig.Approvals.RequestAsync(_set, " ", Ct);
        Assert.False(ticket.Accepted);
    }

    [Fact]
    public async Task Pending_change_is_not_applied()
    {
        var rig = new Rig();
        await rig.Approvals.RequestAsync(_set, "owner-1", Ct);

        Assert.Equal(0, await rig.Sweeper.SweepOnceAsync(Ct));
        Assert.Single(rig.Ledger.Snapshot());
        Assert.Empty(await rig.Grants.ListAsync(Ct));
    }

    [Fact]
    public async Task Approved_set_is_applied_once_and_consumed()
    {
        var rig = new Rig();
        var ticket = await rig.Approvals.RequestAsync(_set, "owner-1", Ct);
        await rig.Decide(ticket.TicketId!.Value, ApprovalStatus.Approved);

        Assert.Equal(1, await rig.Sweeper.SweepOnceAsync(Ct));
        Assert.Equal(0, await rig.Sweeper.SweepOnceAsync(Ct));

        var grant = Assert.Single(await rig.Grants.ListAsync(Ct));
        Assert.Equal("owner-1", grant.GrantedBy);
        Assert.Equal(ApprovalStatus.Consumed, (await rig.Store.GetAsync(ticket.TicketId!.Value, Ct))!.Status);
        Assert.Empty(rig.Ledger.Snapshot());
    }

    [Fact]
    public async Task Denied_change_is_forgotten_and_not_applied()
    {
        var rig = new Rig();
        var ticket = await rig.Approvals.RequestAsync(_set, "owner-1", Ct);
        await rig.Decide(ticket.TicketId!.Value, ApprovalStatus.Denied);

        Assert.Equal(0, await rig.Sweeper.SweepOnceAsync(Ct));
        Assert.Empty(rig.Ledger.Snapshot());
        Assert.Empty(await rig.Grants.ListAsync(Ct));
    }

    [Fact]
    public async Task Sweeper_idles_when_approvals_are_not_registered()
    {
        var ledger = new InMemoryHiveGrantChangeLedger();
        ledger.Add(Guid.NewGuid(), _set);
        var sweeper = new HiveGrantApprovalSweeper(ledger, new ServiceCollection().BuildServiceProvider(), TimeProvider.System, NullLogger<HiveGrantApprovalSweeper>.Instance);

        Assert.Equal(0, await sweeper.SweepOnceAsync(Ct));
        Assert.Single(ledger.Snapshot());
    }
}
