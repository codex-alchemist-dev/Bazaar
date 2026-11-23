using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Tests.Hive;

/// <summary>The wire handler for <c>hive.grant.set</c> and <c>hive.grant.revoke</c>, the fail-closed approvals default, and applying an approved change.</summary>
public sealed class HiveGrantWireTests
{
    private static readonly HiveCaller _owner = new("owner", IsOwner: true);
    private static readonly HiveGrantChangeRequest _request = new("project-a", "project-b", HiveKinds.Name(HiveKind.Procedure));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingApprovals _approvals = new();

    private HiveGrantWireHandler Handler => new(_approvals);

    [Fact]
    public async Task Owner_set_is_queued_for_approval_and_not_applied()
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantSet, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: true, Reason: null, ApprovalRequired: true), reply);
        var (change, requestedBy) = Assert.Single(_approvals.Requests);
        Assert.Equal(new HiveGrantChange(HiveGrantAction.Set, "project-a", "project-b", HiveKind.Procedure), change);
        Assert.Equal("owner", requestedBy);
    }

    [Fact]
    public async Task Owner_revoke_is_queued_for_approval_and_not_applied()
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantRevoke, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: true, Reason: null, ApprovalRequired: true), reply);
        Assert.Equal(HiveGrantAction.Revoke, Assert.Single(_approvals.Requests).Change.Action);
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

    [Theory]
    [InlineData("project-a", "project-a", "procedure")]
    [InlineData("", "project-b", "procedure")]
    [InlineData("project-a", "project-b", "preference")]
    public async Task Invalid_requests_are_refused_and_not_queued(string from, string to, string kind)
    {
        var reply = await Handler.HandleAsync(WireTypes.HiveGrantSet, new HiveGrantChangeRequest(from, to, kind), _owner, Ct);

        Assert.False(reply.Accepted);
        Assert.False(reply.ApprovalRequired);
        Assert.NotNull(reply.Reason);
        Assert.Empty(_approvals.Requests);
    }

    [Fact]
    public async Task Unknown_wire_type_and_blank_principal_are_refused()
    {
        Assert.Equal("unknown wire type", (await Handler.HandleAsync("hive.other", _request, _owner, Ct)).Reason);
        Assert.False((await Handler.HandleAsync(WireTypes.HiveGrantSet, _request, new HiveCaller(" ", IsOwner: true), Ct)).Accepted);
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
    public async Task Default_approvals_refuse_everything()
    {
        var handler = new HiveGrantWireHandler(new DenyAllHiveGrantApprovals());

        var reply = await handler.HandleAsync(WireTypes.HiveGrantSet, _request, _owner, Ct);

        Assert.Equal(new HiveGrantChangeReply(Accepted: false, Reason: "approvals not configured", ApprovalRequired: false), reply);
    }

    [Fact]
    public async Task Approved_set_adds_the_grant_and_approved_revoke_removes_it()
    {
        var grants = new InMemoryHiveGrantStore();
        var vault = new FakeHiveVault();
        var options = new HiveCopyOptions();
        var service = new HiveCopyService(new HiveGrantPolicy(grants, options), grants, new InMemoryHiveCopyLedger(), vault, vault, options, null);
        var applier = new HiveGrantApplier(grants, service);

        var set = new HiveGrantChange(HiveGrantAction.Set, "project-a", "project-b", HiveKind.Procedure);
        Assert.Null(await applier.ApplyApprovedAsync(set, "owner", Ct));
        Assert.True(await grants.IsGrantedAsync("project-a", "project-b", HiveKind.Procedure, Ct));
        Assert.Equal("owner", Assert.Single(await grants.ListAsync(Ct)).GrantedBy);

        var revoke = await applier.ApplyApprovedAsync(set with { Action = HiveGrantAction.Revoke }, "owner", Ct);
        Assert.NotNull(revoke);
        Assert.Empty(revoke.DeprecatedPaths);
        Assert.False(await grants.IsGrantedAsync("project-a", "project-b", HiveKind.Procedure, Ct));
    }

    private sealed class RecordingApprovals : IHiveGrantApprovals
    {
        public List<(HiveGrantChange Change, string RequestedBy)> Requests { get; } = [];

        public HiveApprovalTicket? Next { get; set; }

        public Task<HiveApprovalTicket> RequestAsync(HiveGrantChange change, string requestedBy, CancellationToken ct)
        {
            if (Next is { } refused)
            {
                return Task.FromResult(refused);
            }

            Requests.Add((change, requestedBy));
            return Task.FromResult(HiveApprovalTicket.Queued(Guid.NewGuid()));
        }
    }
}
