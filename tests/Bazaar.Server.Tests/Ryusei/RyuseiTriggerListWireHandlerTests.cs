using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The <c>ryusei.trigger.list</c> wire handler (doc 38, Layer 2): the owner reads a project's triggers as board chips. The checks
/// that need no database run without a fixture. The owner read runs against PostgreSQL and is covered in CI only.
/// </summary>
public sealed class RyuseiTriggerListWireRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonWireCodec _codec = new();

    private static readonly NpgsqlDataSource _unused = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=unused").Build();

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

    private static WireRequestContext Request(IWireConnection connection, Guid projectId) =>
        new(connection, new Envelope(1, WireTypes.RyuseiList, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload(new RyuseiTriggerListRequest(projectId))));

    [Fact]
    public async Task An_empty_project_id_is_a_bad_request()
    {
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));

        var reply = await new RyuseiTriggerListWireHandler(_unused).HandleAsync(Request(owner, Guid.Empty), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task A_non_owner_gets_not_found_and_no_triggers()
    {
        var agent = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "agent"));

        var reply = await new RyuseiTriggerListWireHandler(_unused).HandleAsync(Request(agent, Guid.CreateVersion7()), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }

    [Fact]
    public void The_list_is_served_on_its_own_wire_type()
    {
        Assert.Equal(WireTypes.RyuseiList, new RyuseiTriggerListWireHandler(_unused).Type);
    }
}

/// <summary>The owner's trigger list over PostgreSQL (doc 38, Layer 2). Runs in CI; this container has no Docker.</summary>
public sealed class RyuseiTriggerListWireHandlerTests(KanbanDb db) : IClassFixture<KanbanDb>
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

    [Fact]
    public async Task The_owner_sees_each_trigger_of_the_project_with_its_task()
    {
        SkipWithoutDatabase();
        var project = Guid.CreateVersion7();
        var task = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'list test', 'active')"))
        {
            cmd.Parameters.AddWithValue("id", agent);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var spec = new RyuseiTimerTriggerSpec(_base.AddHours(1));
        var row = new NewRyuseiTrigger(
            Guid.CreateVersion7(), project, Guid.CreateVersion7(), spec.Kind, [agent], "check the build",
            RyuseiSpecJson.Encode(spec), TaskId: task, AttachGcId: null, RyuseiPingMode.None, _base.AddHours(1),
            MaxFires: null, ExpiresAt: null, ChainDepth: 0);
        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await new RyuseiTriggerRepository(db.DataSource).InsertAsync(tx, row, Ct);
            await tx.CommitAsync(Ct);
        }

        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));
        var reply = await new RyuseiTriggerListWireHandler(db.DataSource).HandleAsync(
            new WireRequestContext(owner, new Envelope(1, WireTypes.RyuseiList, EnvelopeKind.Request, "req-1", null, null, null, null,
                _codec.EncodePayload(new RyuseiTriggerListRequest(project)))),
            Ct);

        Assert.True(reply.IsOk);
        var list = _codec.DecodePayload<RyuseiTriggerListReply>(new Envelope(1, WireTypes.RyuseiList, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, reply.Payload));
        var chip = Assert.Single(list.Triggers);
        Assert.Equal(row.TriggerId, chip.TriggerId);
        Assert.Equal(task, chip.TaskId);
        Assert.Equal("timer", chip.Kind);
        Assert.Equal("active", chip.State);
        Assert.Equal("check the build", chip.Purpose);
    }
}
