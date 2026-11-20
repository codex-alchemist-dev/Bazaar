using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Hive.Tests;

/// <summary>The wire handler for <c>hive.grant.set</c> and <c>hive.grant.revoke</c>, and the approval path it queues into.</summary>
public sealed class HiveGrantWireHandlerTests
{
    private static readonly ProjectId _a = new("project-a");
    private static readonly ProjectId _b = new("project-b");
    private static readonly HiveCaller _owner = new("owner", IsOwner: true);
    private static readonly HiveGrantChangeRequest _request = new("project-a", "project-b", HiveKinds.Procedure);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingApprovals _approvals = new();

    private HiveGrantWireHandler Handler => new(_approvals);

    [Fact]
    public async Task Owner_set_is_queued_for_approval_and_not_applied()
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantSet, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: true, Reason: null, ApprovalRequired: true), reply);
        var (change, requestedBy) = Assert.Single(_approvals.Requests);
        Assert.Equal(new HiveGrantChange(HiveGrantAction.Set, _a, _b, HiveKinds.Procedure), change);
        Assert.Equal("owner", requestedBy);
    }

    [Fact]
    public async Task Owner_revoke_is_queued_for_approval_and_not_applied()
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantRevoke, _request, _owner, Ct);

        Assert.True(reply.Accepted);
        Assert.True(reply.ApprovalRequired);
        Assert.Null(reply.Reason);
        var (change, _) = Assert.Single(_approvals.Requests);
        Assert.Equal(HiveGrantAction.Revoke, change.Action);
    }

    [Theory]
    [InlineData(WireTypes.HiveGrantSet)]
    [InlineData(WireTypes.HiveGrantRevoke)]
    public async Task Non_owner_is_refused_before_anything_is_queued(string wireType)
    {
        var reply = await Handler.HandleAsync(wireType, _request, new HiveCaller("agent-x", IsOwner: false), Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: false, Reason: "owner only", ApprovalRequired: false), reply);
        Assert.Empty(_approvals.Requests);
    }

    [Fact]
    public async Task Approval_port_refusal_is_passed_back_as_refusal()
    {
        _approvals.Next = HiveApprovalTicket.Refused("queue full");

        var reply = await Handler.HandleAsync(WireTypes.HiveGrantSet, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: false, Reason: "queue full", ApprovalRequired: false), reply);
    }

    [Fact]
    public async Task Deny_all_default_refuses_every_change()
    {
        var handler = new HiveGrantWireHandler(new DenyAllHiveGrantApprovals());

        var reply = await handler.HandleAsync(WireTypes.HiveGrantSet, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: false, Reason: "approvals not configured", ApprovalRequired: false), reply);
    }

    [Fact]
    public void Registration_defaults_approvals_to_deny_all()
    {
        using var provider = new ServiceCollection().AddBazaarHive().BuildServiceProvider();

        Assert.IsType<DenyAllHiveGrantApprovals>(provider.GetRequiredService<IHiveGrantApprovals>());
        Assert.IsType<HiveGrantWireHandler>(provider.GetRequiredService<IHiveGrantWireHandler>());
    }

    [Fact]
    public async Task Registration_keeps_an_approvals_port_the_host_registered_first()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHiveGrantApprovals>(_approvals);
        services.AddBazaarHive();
        using var provider = services.BuildServiceProvider();

        Assert.Same(_approvals, provider.GetRequiredService<IHiveGrantApprovals>());
    }

    [Theory]
    [InlineData("project-a", "project-a", HiveKinds.Procedure, "A grant needs two different projects.")]
    [InlineData("", "project-b", HiveKinds.Procedure, "A grant needs two different projects.")]
    [InlineData("project-a", " ", HiveKinds.Procedure, "A grant needs two different projects.")]
    [InlineData("project-a", "project-b", "lesson-dump", "cannot cross projects")]
    public async Task Validation_errors_become_refusals_with_the_argument_message(string from, string to, string kind, string reasonPart)
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantSet, new HiveGrantChangeRequest(from, to, kind), _owner, Ct);

        Assert.False(reply.Accepted);
        Assert.False(reply.ApprovalRequired);
        Assert.Contains(reasonPart, reply.Reason);
        Assert.Empty(_approvals.Requests);
    }

    [Fact]
    public async Task Blank_principal_is_refused_with_the_grant_message_for_set()
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantSet, _request, new HiveCaller("  ", IsOwner: true), Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: false, Reason: "A grant records who granted it.", ApprovalRequired: false), reply);
        Assert.Empty(_approvals.Requests);
    }

    [Fact]
    public async Task Blank_principal_is_refused_with_the_revoke_message_for_revoke()
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantRevoke, _request, new HiveCaller("", IsOwner: true), Ct);

        Assert.Equal("A revoke records who revoked it.", reply.Reason);
        Assert.Empty(_approvals.Requests);
    }

    [Theory]
    [InlineData("hive.grant.list")]
    [InlineData("hive.grants.set")]
    [InlineData("")]
    public async Task Unknown_wire_type_is_refused_and_nothing_is_queued(string wireType)
    {
        var reply = await Handler.HandleAsync(wireType, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: false, Reason: "unknown wire type", ApprovalRequired: false), reply);
        Assert.Empty(_approvals.Requests);
    }

    [Fact]
    public async Task Infrastructure_failure_from_the_approvals_port_throws()
    {
        _approvals.Throw = new InvalidOperationException("approvals store down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler.HandleAsync(WireTypes.HiveGrantSet, _request, _owner, Ct));
    }

    [Fact]
    public async Task Approved_set_applies_the_grant_through_the_service_with_the_approver_recorded()
    {
        var (service, grants, audit) = Service();
        var change = new HiveGrantChange(HiveGrantAction.Set, _a, _b, HiveKinds.Procedure);

        var applied = await service.ApplyApprovedAsync(change, "owner", Ct);

        Assert.True(applied);
        Assert.True(await grants.ExistsAsync(_a, _b, HiveKinds.Procedure, Ct));
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("owner", entry.Actor);
        Assert.Equal("hive.grant.added", entry.Kind);
    }

    [Fact]
    public async Task Approved_revoke_removes_the_grant_through_the_service()
    {
        var (service, grants, _) = Service();
        await service.ApplyApprovedAsync(new HiveGrantChange(HiveGrantAction.Set, _a, _b, HiveKinds.Procedure), "owner", Ct);

        var applied = await service.ApplyApprovedAsync(new HiveGrantChange(HiveGrantAction.Revoke, _a, _b, HiveKinds.Procedure), "owner", Ct);

        Assert.True(applied);
        Assert.False(await grants.ExistsAsync(_a, _b, HiveKinds.Procedure, Ct));
    }

    [Fact]
    public async Task Approved_change_for_a_missing_grant_applies_nothing()
    {
        var (service, _, audit) = Service();

        var applied = await service.ApplyApprovedAsync(new HiveGrantChange(HiveGrantAction.Revoke, _a, _b, HiveKinds.Procedure), "owner", Ct);

        Assert.False(applied);
        Assert.Empty(audit.Entries);
    }

    private static (HiveGrantService Service, InMemoryGrantStore Grants, FakeAudit Audit) Service()
    {
        var grants = new InMemoryGrantStore();
        var audit = new FakeAudit();
        var service = new HiveGrantService(grants, new InMemoryCopyStore(), new FakeVaultWriter(), audit, new FakeClock());
        return (service, grants, audit);
    }

    /// <summary>Records each request and answers with <see cref="Next"/>, or throws <see cref="Throw"/> when set.</summary>
    private sealed class RecordingApprovals : IHiveGrantApprovals
    {
        public List<(HiveGrantChange Change, string RequestedBy)> Requests { get; } = [];

        public HiveApprovalTicket Next { get; set; } = HiveApprovalTicket.Queued(Guid.Empty);

        public Exception? Throw { get; set; }

        public Task<HiveApprovalTicket> RequestAsync(HiveGrantChange change, string requestedBy, CancellationToken ct)
        {
            if (Throw is not null)
                throw Throw;
            Requests.Add((change, requestedBy));
            return Task.FromResult(Next);
        }
    }
}
