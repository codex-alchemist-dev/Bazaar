using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Server.Kanban.Wire;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The <c>board.snapshot</c> wire handler over the real task service (doc 12, board view).</summary>
public sealed class BoardSnapshotHandlerTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static readonly JsonWireCodec _codec = new();

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
        new(connection, new Envelope(1, WireTypes.BoardSnapshot, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload(new KanbanBoardRequest(projectId))));

    private async Task<(Guid Project, Guid Task)> SeedAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var project = Guid.CreateVersion7();
        var task = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(task, project, "Ship board", TaskState.Ready, null, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return (project, task);
    }

    private BoardSnapshotHandler Handler() =>
        new(new TaskService(db.DataSource), new OwnerOnlyBoardAccess());

    [Fact]
    public async Task The_owner_gets_the_whole_board_for_the_project()
    {
        SkipWithoutDatabase();
        var (project, task) = await SeedAsync();
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));

        var reply = await Handler().HandleAsync(Request(owner, project), Ct);

        Assert.True(reply.IsOk);
        var snapshot = _codec.DecodePayload<KanbanBoardSnapshot>(new Envelope(1, WireTypes.BoardSnapshot, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, reply.Payload));
        var card = Assert.Single(snapshot.Cards);
        Assert.Equal(task, card.Id);
        Assert.Equal("ready", card.State);
    }

    [Fact]
    public async Task A_non_owner_gets_not_found_for_the_same_board()
    {
        SkipWithoutDatabase();
        var (project, _) = await SeedAsync();
        var agent = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "agent"));

        var reply = await Handler().HandleAsync(Request(agent, project), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }

    [Fact]
    public async Task An_empty_project_id_is_a_bad_request_before_any_read()
    {
        SkipWithoutDatabase();
        var owner = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), "owner"));

        var reply = await Handler().HandleAsync(Request(owner, Guid.Empty), Ct);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }
}
