using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Wire;
using Bazaar.Server.TimeMachine;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The host adapters for <c>replay.frame</c> and <c>replay.fork</c>, and the migration sources the Time Machine modules add.</summary>
public sealed class ReplayWireAdapterTests
{
    private static readonly JsonWireCodec _codec = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeConnection(PrincipalRef principal) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = principal;
        public string DeviceId => "test";
        public string DeviceClass => "test";
        public IWireCodec Codec => _codec;
        public IReadOnlyList<StreamCursor> HelloCursors => [];
        public Bazaar.Server.Wire.Sync.SubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    /// <summary>One GC with five messages that only the allowed principal may read.</summary>
    private sealed class FakeLog(Guid gcId, Guid allowed) : IGcLogReader, IGcReadAccess
    {
        public ValueTask<GcReadGrant> GetGrantAsync(Guid id, GcPrincipal viewer, CancellationToken ct) =>
            ValueTask.FromResult(id == gcId && viewer.Id == allowed ? GcReadGrant.Full : GcReadGrant.Denied);

        public Task<long> GetHeadSeqAsync(Guid id, CancellationToken ct) => Task.FromResult(5L);

        public async IAsyncEnumerable<GcEvent> ReadAsync(
            Guid id, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
        {
            var grant = await GetGrantAsync(id, viewer, ct);
            if (!grant.Allowed)
                throw new GcAccessDeniedException(id);
            for (var seq = afterSeq + 1; seq <= 5 && seq - afterSeq <= limit; seq++)
            {
                yield return new GcEvent(
                    gcId, seq, Guid.NewGuid(), GcEventKinds.Message, AuthorKind.Human, allowed, SourceTrust.MemberHuman, false, null,
                    Guid.NewGuid(), 1, null, null, "{\"text\":\"m\"}", DateTimeOffset.UnixEpoch.AddMinutes(seq));
            }
        }
    }

    private sealed class Provisioner : IForkTargetProvisioner
    {
        public Task<ForkTarget> ProvisionAsync(Guid requestId, Guid sourceGcId, long effectiveSeq, GcPrincipal requester, CancellationToken ct) =>
            Task.FromResult(new ForkTarget(Guid.NewGuid(), "abc123"));
    }

    private static ServiceProvider Services(FakeLog log, bool withProvisioner)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcLogReader>(log);
        services.AddSingleton<IGcReadAccess>(log);
        services.AddBazaarTimeMachine();
        // The real Postgres link store needs a data source; the in-memory one is the module's own test double.
        services.AddSingleton<IForkLinkStore, InMemoryForkLinkStore>();
        if (withProvisioner)
            services.AddSingleton<IForkTargetProvisioner, Provisioner>();
        services.AddBazaarReplayWire();
        return services.BuildServiceProvider();
    }

    private static IWireRequestHandler Handler(ServiceProvider sp, string type) =>
        sp.GetServices<IWireRequestHandler>().Single(h => h.Type == type);

    private static WireRequestContext Request<T>(PrincipalRef who, string type, T payload) =>
        new(new FakeConnection(who), new Envelope(1, type, EnvelopeKind.Request, "req-1", null, null, null, null, _codec.EncodePayload(payload)));

    private static T Decode<T>(string type, WireReply reply) =>
        _codec.DecodePayload<T>(new Envelope(1, type, EnvelopeKind.Response, "req-1", "req-1", null, null, null, reply.Payload));

    [Fact]
    public async Task Frame_decodes_the_payload_and_calls_the_handler()
    {
        var gc = Guid.CreateVersion7();
        var owner = new PrincipalRef(Guid.CreateVersion7(), "owner");
        await using var sp = Services(new FakeLog(gc, owner.Id), withProvisioner: true);

        var reply = await Handler(sp, WireTypes.ReplayFrame).HandleAsync(Request(owner, WireTypes.ReplayFrame, new ReplayFrameRequest(gc, 3)), Ct);

        Assert.True(reply.IsOk);
        var result = Decode<ReplayFrameResult>(WireTypes.ReplayFrame, reply);
        Assert.Equal(3, result.Seq);
        Assert.Equal(5, result.Head);
        Assert.Equal(3, result.Frame.Messages.Count);
    }

    [Fact]
    public async Task Denied_frame_and_fork_are_not_found()
    {
        var gc = Guid.CreateVersion7();
        var owner = new PrincipalRef(Guid.CreateVersion7(), "owner");
        var outsider = new PrincipalRef(Guid.CreateVersion7(), "human");
        await using var sp = Services(new FakeLog(gc, owner.Id), withProvisioner: true);

        var frame = await Handler(sp, WireTypes.ReplayFrame).HandleAsync(Request(outsider, WireTypes.ReplayFrame, new ReplayFrameRequest(gc, 3)), Ct);
        var fork = await Handler(sp, WireTypes.ReplayFork)
            .HandleAsync(Request(outsider, WireTypes.ReplayFork, new ReplayForkRequest(gc, 3, Guid.NewGuid())), Ct);

        Assert.False(frame.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, frame.Error!.Code);
        Assert.False(fork.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, fork.Error!.Code);
    }

    [Fact]
    public async Task Fork_calls_the_handler_and_a_repeat_is_not_created()
    {
        var gc = Guid.CreateVersion7();
        var owner = new PrincipalRef(Guid.CreateVersion7(), "owner");
        var requestId = Guid.CreateVersion7();
        await using var sp = Services(new FakeLog(gc, owner.Id), withProvisioner: true);
        var handler = Handler(sp, WireTypes.ReplayFork);

        var first = await handler.HandleAsync(Request(owner, WireTypes.ReplayFork, new ReplayForkRequest(gc, 3, requestId)), Ct);
        var again = await handler.HandleAsync(Request(owner, WireTypes.ReplayFork, new ReplayForkRequest(gc, 3, requestId)), Ct);

        var one = Decode<ReplayForkResult>(WireTypes.ReplayFork, first);
        var two = Decode<ReplayForkResult>(WireTypes.ReplayFork, again);
        Assert.True(one.Created);
        Assert.False(two.Created);
        Assert.Equal(one.NewGc, two.NewGc);
    }

    [Fact]
    public async Task Fork_without_a_provisioner_answers_unsupported_and_frames_still_work()
    {
        var gc = Guid.CreateVersion7();
        var owner = new PrincipalRef(Guid.CreateVersion7(), "owner");
        await using var sp = Services(new FakeLog(gc, owner.Id), withProvisioner: false);

        var fork = await Handler(sp, WireTypes.ReplayFork)
            .HandleAsync(Request(owner, WireTypes.ReplayFork, new ReplayForkRequest(gc, 3, Guid.NewGuid())), Ct);
        var frame = await Handler(sp, WireTypes.ReplayFrame).HandleAsync(Request(owner, WireTypes.ReplayFrame, new ReplayFrameRequest(gc, 3)), Ct);

        Assert.False(fork.IsOk);
        Assert.Equal(WireErrorCodes.Unsupported, fork.Error!.Code);
        Assert.True(frame.IsOk);
    }

    [Fact]
    public async Task A_bad_request_is_bad_request()
    {
        var owner = new PrincipalRef(Guid.CreateVersion7(), "owner");
        await using var sp = Services(new FakeLog(Guid.CreateVersion7(), owner.Id), withProvisioner: true);

        var reply = await Handler(sp, WireTypes.ReplayFrame)
            .HandleAsync(Request(owner, WireTypes.ReplayFrame, new ReplayFrameRequest(Guid.Empty, 0)), Ct);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public void Host_migrations_include_the_time_machine_playtest_and_what_if_namespaces()
    {
        var namespaces = HostMigrations.Source().Namespaces();

        Assert.Contains("time_machine", namespaces);
        Assert.Contains("playtest", namespaces);
        Assert.Contains("what_if", namespaces);
    }
}
