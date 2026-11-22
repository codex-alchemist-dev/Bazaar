using Bazaar.Contracts.Gc;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.GcMetadata.Tests;

public class DmAccessServiceTests
{
    private static readonly Guid _gc = Guid.NewGuid();
    private static readonly DmActor _alice = new(Guid.NewGuid(), PrincipalKind.Human);
    private static readonly DmActor _bob = new(Guid.NewGuid(), PrincipalKind.Agent);
    private static readonly DmActor _owner = new(Guid.NewGuid(), PrincipalKind.Owner);
    private static readonly DmActor _carol = new(Guid.NewGuid(), PrincipalKind.Human);
    private static readonly DmActor _agent = new(Guid.NewGuid(), PrincipalKind.Agent);

    private sealed class Rig
    {
        public readonly FakeLedger Ledger = new();
        public readonly FakeEvents Events = new();
        public readonly FakeAudit Audit = new();
        public readonly DmAccessService Service;

        public Rig(DmAccessOptions? options = null) =>
            Service = new DmAccessService(new FakeDirectory(), Ledger, Events, Audit, new StaticDmAccessOptionsSource(options ?? new DmAccessOptions()), new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero)));
    }

    private sealed class FakeDirectory : IDmDirectory
    {
        public Task<DmInfo?> FindAsync(Guid gcId, CancellationToken ct) =>
            Task.FromResult(gcId == _gc ? new DmInfo(_gc, [_alice.Id, _bob.Id]) : null);
    }

    private sealed class FakeLedger : IDmAccessLedger
    {
        public readonly List<DmAccessRequest> Rows = [];

        public Task<IReadOnlyList<DmAccessRequest>> ListAsync(Guid gcId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DmAccessRequest>>(Rows.Where(r => r.GcId == gcId).ToList());

        public Task<DmAccessRequest?> GetAsync(Guid requestId, CancellationToken ct) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.Id == requestId));

        public Task<DmAccessRequest> CreateAsync(Guid gcId, Guid requester, DateTimeOffset at, CancellationToken ct)
        {
            var row = new DmAccessRequest(Guid.NewGuid(), gcId, requester, DmAccessStatus.Pending, null, at);
            Rows.Add(row);
            return Task.FromResult(row);
        }

        public Task<bool> ResolveAsync(Guid requestId, DmAccessStatus decision, Guid decidedBy, DateTimeOffset at, CancellationToken ct)
        {
            var i = Rows.FindIndex(r => r.Id == requestId && r.Status == DmAccessStatus.Pending);
            if (i < 0)
                return Task.FromResult(false);
            Rows[i] = Rows[i] with { Status = decision, DecidedBy = decidedBy };
            return Task.FromResult(true);
        }

        public Task SetDecidedSeqAsync(Guid requestId, long seq, CancellationToken ct)
        {
            var i = Rows.FindIndex(r => r.Id == requestId);
            Rows[i] = Rows[i] with { DecidedSeq = seq };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEvents : IDmAccessEvents
    {
        public readonly List<string> Kinds = [];

        public Task<long> AppendAsync(Guid gcId, string kind, Guid requestId, Guid requester, Guid actor, CancellationToken ct)
        {
            Kinds.Add(kind);
            return Task.FromResult(100L + Kinds.Count);
        }
    }

    private sealed class FakeAudit : IAuditLog
    {
        public readonly List<AuditEntry> Entries = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_records_one_pending_row_one_event_and_one_audit_entry()
    {
        var rig = new Rig();

        var result = await rig.Service.RequestAsync(_carol, _gc, Ct);

        Assert.Equal(DmAccessOutcome.Requested, result.Outcome);
        Assert.Equal(DmAccessStatus.Pending, Assert.Single(rig.Ledger.Rows).Status);
        Assert.Equal(["access_requested"], rig.Events.Kinds);
        Assert.Equal("dm.access.requested", Assert.Single(rig.Audit.Entries).Kind);
    }

    [Fact]
    public async Task A_repeat_request_returns_the_pending_one()
    {
        var rig = new Rig();
        var first = await rig.Service.RequestAsync(_carol, _gc, Ct);

        var second = await rig.Service.RequestAsync(_carol, _gc, Ct);

        Assert.Equal(DmAccessOutcome.AlreadyPending, second.Outcome);
        Assert.Equal(first.Request!.Id, second.Request!.Id);
        Assert.Single(rig.Ledger.Rows);
    }

    [Fact]
    public async Task Members_and_the_owner_need_no_request()
    {
        var rig = new Rig();

        Assert.Equal(DmAccessOutcome.AlreadyAllowed, (await rig.Service.RequestAsync(_alice, _gc, Ct)).Outcome);
        Assert.Equal(DmAccessOutcome.AlreadyAllowed, (await rig.Service.RequestAsync(_owner, _gc, Ct)).Outcome);
        Assert.Empty(rig.Ledger.Rows);
    }

    [Fact]
    public async Task Request_for_an_unknown_gc_is_not_a_dm()
    {
        var rig = new Rig();

        Assert.Equal(DmAccessOutcome.NotADm, (await rig.Service.RequestAsync(_carol, Guid.NewGuid(), Ct)).Outcome);
    }

    [Fact]
    public async Task Owner_grant_opens_the_read_and_is_audited()
    {
        var rig = new Rig();
        var request = (await rig.Service.RequestAsync(_carol, _gc, Ct)).Request!;
        Assert.False((await rig.Service.AuthorizeReadAsync(_carol, _gc, Ct)).Allowed);

        var result = await rig.Service.DecideAsync(_owner, request.Id, approve: true, Ct);

        Assert.Equal(DmAccessOutcome.Granted, result.Outcome);
        var read = await rig.Service.AuthorizeReadAsync(_carol, _gc, Ct);
        Assert.True(read.Allowed);
        Assert.Equal(DmReadBasis.Grant, read.Basis);
        Assert.Contains("access_granted", rig.Events.Kinds);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == "dm.access.granted" && e.Actor == _owner.Id.ToString("D"));
    }

    [Fact]
    public async Task A_grant_records_the_seq_of_its_system_event_and_a_grant_without_one_reads_nothing()
    {
        var rig = new Rig();
        var request = (await rig.Service.RequestAsync(_carol, _gc, Ct)).Request!;

        var result = await rig.Service.DecideAsync(_owner, request.Id, approve: true, Ct);

        Assert.Equal(102L, result.Request!.DecidedSeq);
        Assert.Equal(102L, rig.Ledger.Rows[0].DecidedSeq);

        rig.Ledger.Rows[0] = rig.Ledger.Rows[0] with { DecidedSeq = null };
        Assert.False((await rig.Service.AuthorizeReadAsync(_carol, _gc, Ct)).Allowed);
    }

    [Fact]
    public async Task Owner_deny_keeps_the_read_closed()
    {
        var rig = new Rig();
        var request = (await rig.Service.RequestAsync(_carol, _gc, Ct)).Request!;

        var result = await rig.Service.DecideAsync(_owner, request.Id, approve: false, Ct);

        Assert.Equal(DmAccessOutcome.Denied, result.Outcome);
        Assert.False((await rig.Service.AuthorizeReadAsync(_carol, _gc, Ct)).Allowed);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == "dm.access.denied");
    }

    [Fact]
    public async Task An_agent_cannot_decide_even_when_it_is_a_dm_member_and_the_refusal_is_audited()
    {
        var rig = new Rig(new DmAccessOptions(DmApprovers.OwnerOrDmMember));
        var request = (await rig.Service.RequestAsync(_carol, _gc, Ct)).Request!;

        var result = await rig.Service.DecideAsync(_bob, request.Id, approve: true, Ct);

        Assert.Equal(DmAccessOutcome.NotAllowed, result.Outcome);
        Assert.Equal(DmAccessStatus.Pending, rig.Ledger.Rows[0].Status);
        Assert.Contains(rig.Audit.Entries, e => e.Kind == "dm.access.refused");
    }

    [Fact]
    public async Task A_member_decides_only_when_the_setting_allows_it()
    {
        var ownerOnly = new Rig();
        var open = new Rig(new DmAccessOptions(DmApprovers.OwnerOrDmMember));
        var r1 = (await ownerOnly.Service.RequestAsync(_carol, _gc, Ct)).Request!;
        var r2 = (await open.Service.RequestAsync(_carol, _gc, Ct)).Request!;

        Assert.Equal(DmAccessOutcome.NotAllowed, (await ownerOnly.Service.DecideAsync(_alice, r1.Id, true, Ct)).Outcome);
        Assert.Equal(DmAccessOutcome.Granted, (await open.Service.DecideAsync(_alice, r2.Id, true, Ct)).Outcome);
    }

    [Fact]
    public async Task A_resolved_request_cannot_be_decided_again()
    {
        var rig = new Rig();
        var request = (await rig.Service.RequestAsync(_carol, _gc, Ct)).Request!;
        await rig.Service.DecideAsync(_owner, request.Id, true, Ct);

        Assert.Equal(DmAccessOutcome.NotPending, (await rig.Service.DecideAsync(_owner, request.Id, false, Ct)).Outcome);
        Assert.Equal(DmAccessOutcome.NotPending, (await rig.Service.DecideAsync(_owner, Guid.NewGuid(), true, Ct)).Outcome);
    }

    [Fact]
    public async Task Owner_reads_without_a_request_and_each_read_is_audited_with_the_marker()
    {
        var rig = new Rig();

        var read = await rig.Service.AuthorizeReadAsync(_owner, _gc, Ct);

        Assert.True(read.Allowed);
        Assert.Equal(DmReadBasis.Owner, read.Basis);
        Assert.True(read.ShowOwnerReadMarker);
        Assert.Equal("dm.owner_read", Assert.Single(rig.Audit.Entries).Kind);
        await rig.Service.AuthorizeReadAsync(_owner, _gc, Ct);
        Assert.Equal(2, rig.Audit.Entries.Count);
    }

    [Fact]
    public async Task Marker_follows_the_setting()
    {
        var rig = new Rig(new DmAccessOptions(ShowOwnerReadMarker: false));

        Assert.False((await rig.Service.AuthorizeReadAsync(_owner, _gc, Ct)).ShowOwnerReadMarker);
    }

    [Fact]
    public async Task Members_read_without_an_audit_entry_and_outsiders_and_agents_cannot()
    {
        var rig = new Rig();

        var member = await rig.Service.AuthorizeReadAsync(_alice, _gc, Ct);
        var outsider = await rig.Service.AuthorizeReadAsync(_carol, _gc, Ct);
        var agent = await rig.Service.AuthorizeReadAsync(_agent, _gc, Ct);

        Assert.Equal(DmReadBasis.Member, member.Basis);
        Assert.False(outsider.Allowed);
        Assert.False(agent.Allowed);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public async Task An_agent_may_request_access()
    {
        var rig = new Rig();

        Assert.Equal(DmAccessOutcome.Requested, (await rig.Service.RequestAsync(_agent, _gc, Ct)).Outcome);
    }
}
