using Bazaar.Contracts.Gc;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine.Tests;

public class ReplayWireHandlerTests
{
    private static readonly GcPrincipal _owner = new(Guid.NewGuid(), PrincipalKind.Owner, "Owner");
    private static readonly GcPrincipal _outsider = new(Guid.NewGuid(), PrincipalKind.Human, "Outsider");

    private sealed class FixedVault : IVaultReplaySource
    {
        public Task<ReplayVaultState> GetAsync(Guid gcId, long seq, GcPrincipal viewer, CancellationToken ct) =>
            Task.FromResult(new ReplayVaultState("c" + seq));
    }

    private sealed class Provisioner : IForkTargetProvisioner
    {
        public int Calls { get; private set; }

        public Task<ForkTarget> ProvisionAsync(Guid requestId, Guid sourceGcId, long effectiveSeq, GcPrincipal requester, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ForkTarget(Guid.NewGuid(), "abc123"));
        }
    }

    private static (ReplayWireHandler Handler, FakeLog Log, Provisioner Provisioner) Make()
    {
        var log = new FakeLog();
        log.Grants[_owner.Id] = GcReadGrant.Full;
        for (var i = 0; i < 5; i++)
        {
            log.Message(Guid.NewGuid(), 1, "m" + i);
        }

        var provisioner = new Provisioner();
        var handler = new ReplayWireHandler(
            new ReplayProjector(log, log, vault: new FixedVault()),
            new ForkService(log, log, new InMemoryForkLinkStore(), provisioner),
            log,
            log);
        return (handler, log, provisioner);
    }

    [Fact]
    public async Task Frame_maps_messages_and_parts()
    {
        var (handler, log, _) = Make();

        var reply = await handler.FrameAsync(_owner, new ReplayFrameRequest(log.GcId, 3), CancellationToken.None);

        Assert.True(reply.IsOk);
        var result = reply.Value!;
        Assert.Equal(3, result.Seq);
        Assert.Equal(5, result.Head);
        Assert.Equal(3, result.Frame.Messages.Count);
        Assert.True(result.Frame.Vault.Available);
        Assert.Equal("c3", result.Frame.Vault.Value!.Commit);
        Assert.False(result.Frame.Kanban.Available);
        Assert.NotNull(result.Frame.Kanban.Reason);
    }

    [Fact]
    public async Task Denied_gc_is_not_found_for_both_requests()
    {
        var (handler, log, provisioner) = Make();

        var frame = await handler.FrameAsync(_outsider, new ReplayFrameRequest(log.GcId, 3), CancellationToken.None);
        var fork = await handler.ForkAsync(_outsider, new ReplayForkRequest(log.GcId, 3, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(WireErrorCodes.NotFound, frame.ErrorCode);
        Assert.Equal(WireErrorCodes.NotFound, fork.ErrorCode);
        Assert.Equal(0, provisioner.Calls);
    }

    [Fact]
    public async Task Seq_above_the_grant_is_clamped_and_head_is_the_visible_head()
    {
        var (handler, log, _) = Make();
        var left = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Human, "Left");
        log.Grants[left.Id] = GcReadGrant.Through(2);

        var reply = await handler.FrameAsync(left, new ReplayFrameRequest(log.GcId, 5), CancellationToken.None);

        Assert.True(reply.IsOk);
        Assert.Equal(2, reply.Value!.Seq);
        Assert.Equal(2, reply.Value.Head);
        Assert.Equal(2, reply.Value.Frame.Messages.Count);
    }

    [Fact]
    public async Task Fork_repeat_returns_the_first_result_with_created_false()
    {
        var (handler, log, provisioner) = Make();
        var request = new ReplayForkRequest(log.GcId, 3, Guid.NewGuid());

        var first = await handler.ForkAsync(_owner, request, CancellationToken.None);
        var again = await handler.ForkAsync(_owner, request, CancellationToken.None);

        Assert.True(first.Value!.Created);
        Assert.False(again.Value!.Created);
        Assert.Equal(first.Value.NewGc, again.Value.NewGc);
        Assert.Equal("abc123", again.Value.VaultCommit);
        Assert.Equal(1, provisioner.Calls);
        Assert.Equal(5, log.Events.Count);
    }

    [Fact]
    public async Task Fork_above_the_head_dedupes_on_the_effective_seq()
    {
        var (handler, log, provisioner) = Make();
        var id = Guid.NewGuid();

        var a = await handler.ForkAsync(_owner, new ReplayForkRequest(log.GcId, 5, id), CancellationToken.None);
        var b = await handler.ForkAsync(_owner, new ReplayForkRequest(log.GcId, 99, id), CancellationToken.None);

        Assert.False(b.Value!.Created);
        Assert.Equal(a.Value!.NewGc, b.Value.NewGc);
        Assert.Equal(1, provisioner.Calls);
    }

    [Fact]
    public async Task Malformed_requests_are_bad_request()
    {
        var (handler, log, _) = Make();

        var noGc = await handler.FrameAsync(_owner, new ReplayFrameRequest(Guid.Empty, 1), CancellationToken.None);
        var negative = await handler.FrameAsync(_owner, new ReplayFrameRequest(log.GcId, -1), CancellationToken.None);
        var noId = await handler.ForkAsync(_owner, new ReplayForkRequest(log.GcId, 1, Guid.Empty), CancellationToken.None);

        Assert.Equal(WireErrorCodes.BadRequest, noGc.ErrorCode);
        Assert.Equal(WireErrorCodes.BadRequest, negative.ErrorCode);
        Assert.Equal(WireErrorCodes.BadRequest, noId.ErrorCode);
    }
}
