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
        public Bazaar.Server.Wire.Sync.ISubscriptionSession? Subscriptions => null;
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
    public void Missing_gc_read_services_fail_when_the_handlers_are_built()
    {
        var services = new ServiceCollection();
        services.AddBazaarTimeMachine();
        services.AddSingleton<IForkLinkStore, InMemoryForkLinkStore>();
        services.AddBazaarReplayWire();
        using var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetServices<IWireRequestHandler>().ToArray());
    }

    [Fact]
    public async Task Handlers_build_at_startup_without_a_fork_provisioner()
    {
        var log = new FakeLog(Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var sp = Services(log, withProvisioner: false);

        Assert.Equal(2, sp.GetServices<IWireRequestHandler>().Count(h => h.Type is WireTypes.ReplayFrame or WireTypes.ReplayFork));
    }

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

/// <summary>The GC-backed fork target provisioner and how the host binds it.</summary>
public sealed class GcForkTargetProvisionerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingProvisioner : Bazaar.Server.Gc.Write.IGcProvisioner
    {
        public List<(Guid Id, string Title, GcPrincipal Owner)> Created { get; } = [];
        private readonly HashSet<Guid> _ids = [];

        public Task<bool> CreateProjectGcAsync(Guid gcId, string title, GcPrincipal owner, CancellationToken ct)
        {
            var created = _ids.Add(gcId);
            if (created)
                Created.Add((gcId, title, owner));
            return Task.FromResult(created);
        }

        public Task<bool> CreateAsync(Guid gcId, string typeKey, string title, GcPrincipal owner, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Guid?> FindAsync(string typeKey, Guid ownerId, string metadataKey, string metadataValue, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> AddMemberAsync(Guid gcId, GcPrincipal member, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_fork_creates_one_project_gc_for_the_requester_and_a_retry_finds_the_same_one()
    {
        var gcs = new RecordingProvisioner();
        var provisioner = new GcForkTargetProvisioner(gcs);
        var requester = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Owner, "Owner");
        var request = Guid.NewGuid();
        var source = Guid.NewGuid();

        var first = await provisioner.ProvisionAsync(request, source, 7, requester, Ct);
        var again = await provisioner.ProvisionAsync(request, source, 7, requester, Ct);

        Assert.Equal(first.NewGcId, again.NewGcId);
        Assert.Equal(GcForkTargetProvisioner.TargetId(request), first.NewGcId);
        Assert.Null(first.VaultCommit);
        var created = Assert.Single(gcs.Created);
        Assert.Equal(requester, created.Owner);
        Assert.Contains("seq 7", created.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_requests_get_different_target_gcs()
    {
        Assert.NotEqual(GcForkTargetProvisioner.TargetId(Guid.NewGuid()), GcForkTargetProvisioner.TargetId(Guid.NewGuid()));
    }

    [Fact]
    public void The_extension_binds_the_provisioner_only_when_a_gc_provisioner_is_registered()
    {
        var none = new ServiceCollection().AddBazaarGcForkTarget();
        Assert.DoesNotContain(none, d => d.ServiceType == typeof(IForkTargetProvisioner));

        var bound = new ServiceCollection();
        bound.AddSingleton<Bazaar.Server.Gc.Write.IGcProvisioner>(new RecordingProvisioner());
        bound.AddBazaarGcForkTarget();
        using var provider = bound.BuildServiceProvider();
        Assert.IsType<GcForkTargetProvisioner>(provider.GetRequiredService<IForkTargetProvisioner>());
    }
}
