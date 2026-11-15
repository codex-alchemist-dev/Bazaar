using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The <c>ryusei.trigger.pause</c> and <c>ryusei.trigger.resume</c> wire handlers over the real trigger service (doc 38, Layer 2):
/// the owner changes a trigger, and any other principal gets <c>not_found</c>. Uses the Kanban fixture, which migrates every
/// namespace. This container has no Docker, so these tests are not run here; CI is the verifier.
/// </summary>
public sealed class RyuseiTriggerWireHandlerTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _base = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonWireCodec _codec = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

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

    private static WireRequestContext Request(IWireConnection connection, string type, Guid triggerId) =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload(new RyuseiTriggerChangeRequest(triggerId))));

    private RyuseiTriggerWireHandler Pause() =>
        new(new RyuseiTriggerService(db.DataSource, new PostgresAuditLog(db.DataSource)), TimeProvider.System, RyuseiTriggerWireAction.Pause);

    private RyuseiTriggerWireHandler Resume() =>
        new(new RyuseiTriggerService(db.DataSource, new PostgresAuditLog(db.DataSource)), TimeProvider.System, RyuseiTriggerWireAction.Resume);

    private async Task InsertAgentAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'wire test', 'active')");
        cmd.Parameters.AddWithValue("id", agentId);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<RyuseiTriggerRecord> InsertActiveTriggerAsync()
    {
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent);
        var spec = new RyuseiTimerTriggerSpec(_base.AddHours(1));
        var trigger = new NewRyuseiTrigger(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            spec.Kind,
            [agent],
            "check the build",
            RyuseiSpecJson.Encode(spec),
            TaskId: null,
            AttachGcId: null,
            RyuseiPingMode.None,
            _base.AddHours(1),
            MaxFires: null,
            ExpiresAt: null,
            ChainDepth: 0);
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await new RyuseiTriggerRepository(db.DataSource).InsertAsync(tx, trigger, Ct);
        await tx.CommitAsync(Ct);
        return row;
    }

    private async Task<RyuseiTriggerState?> StateOfAsync(Guid triggerId)
    {
        var stored = await new RyuseiTriggerRepository(db.DataSource).GetAsync(triggerId, Ct);
        return stored?.State;
    }

    [Fact]
    public async Task The_owner_pauses_an_active_trigger_and_gets_its_new_version()
    {
        SkipWithoutDatabase();
        var row = await InsertActiveTriggerAsync();
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));

        var reply = await Pause().HandleAsync(Request(owner, WireTypes.RyuseiPause, row.TriggerId), Ct);

        Assert.True(reply.IsOk);
        var change = _codec.DecodePayload<RyuseiTriggerChangeReply>(new Envelope(1, WireTypes.RyuseiPause, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, reply.Payload));
        Assert.Equal(row.TriggerId, change.TriggerId);
        Assert.Equal(RyuseiTriggerState.Paused, await StateOfAsync(row.TriggerId));
    }

    [Fact]
    public async Task The_owner_resumes_a_paused_trigger()
    {
        SkipWithoutDatabase();
        var row = await InsertActiveTriggerAsync();
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));
        Assert.True((await Pause().HandleAsync(Request(owner, WireTypes.RyuseiPause, row.TriggerId), Ct)).IsOk);

        var reply = await Resume().HandleAsync(Request(owner, WireTypes.RyuseiResume, row.TriggerId), Ct);

        Assert.True(reply.IsOk);
        Assert.Equal(RyuseiTriggerState.Active, await StateOfAsync(row.TriggerId));
    }

    [Fact]
    public async Task A_non_owner_gets_not_found_and_the_trigger_is_unchanged()
    {
        SkipWithoutDatabase();
        var row = await InsertActiveTriggerAsync();
        var agent = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "agent"));

        var reply = await Pause().HandleAsync(Request(agent, WireTypes.RyuseiPause, row.TriggerId), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Equal(RyuseiTriggerState.Active, await StateOfAsync(row.TriggerId));
    }

    [Fact]
    public async Task An_unknown_trigger_gets_not_found()
    {
        SkipWithoutDatabase();
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));

        var reply = await Pause().HandleAsync(Request(owner, WireTypes.RyuseiPause, Guid.CreateVersion7()), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }
}

/// <summary>
/// The wire handler checks that need no database: a request with no trigger id, and any principal but the owner. Both are
/// answered before the trigger service is touched, so the service here is never called. No fixture, so these run without Docker.
/// </summary>
public sealed class RyuseiTriggerWireRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonWireCodec _codec = new();

    private static readonly RyuseiTriggerService _unused = new(
        new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=unused").Build(),
        new Bazaar.Server.Security.Audit.Db.PostgresAuditLog(new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=unused").Build()));

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

    private static WireRequestContext Request(IWireConnection connection, Guid triggerId) =>
        new(connection, new Envelope(1, WireTypes.RyuseiPause, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload(new RyuseiTriggerChangeRequest(triggerId))));

    [Fact]
    public async Task An_empty_trigger_id_is_a_bad_request()
    {
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));
        var handler = new RyuseiTriggerWireHandler(_unused, TimeProvider.System, RyuseiTriggerWireAction.Pause);

        var reply = await handler.HandleAsync(Request(owner, Guid.Empty), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task A_non_owner_gets_not_found_without_touching_the_trigger()
    {
        var agent = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "agent"));
        var handler = new RyuseiTriggerWireHandler(_unused, TimeProvider.System, RyuseiTriggerWireAction.Resume);

        var reply = await handler.HandleAsync(Request(agent, Guid.CreateVersion7()), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }

    [Fact]
    public void Each_action_serves_its_own_wire_type()
    {
        Assert.Equal(WireTypes.RyuseiPause, new RyuseiTriggerWireHandler(_unused, TimeProvider.System, RyuseiTriggerWireAction.Pause).Type);
        Assert.Equal(WireTypes.RyuseiResume, new RyuseiTriggerWireHandler(_unused, TimeProvider.System, RyuseiTriggerWireAction.Resume).Type);
    }
}
