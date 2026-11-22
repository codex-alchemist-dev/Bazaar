using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.RyuseiFuda.Kanban.Wire;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The <c>board.assign</c> wire handler over the real task service (doc 12, Assign authority and command).</summary>
public sealed class BoardAssignHandlerTests(KanbanDb db) : IClassFixture<KanbanDb>
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
        public Bazaar.Server.Wire.Sync.ISubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private static readonly FakeConnection _owner = new(new PrincipalRef(Guid.CreateVersion7(), "owner"));

    private static WireRequestContext Request(IWireConnection connection, KanbanAssignRequest body) =>
        new(connection, new Envelope(1, WireTypes.BoardAssign, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload(body)));

    private static KanbanAssignReply Decode(WireReply reply)
    {
        Assert.True(reply.IsOk, reply.Error?.Message);
        return _codec.DecodePayload<KanbanAssignReply>(new Envelope(1, WireTypes.BoardAssign, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, reply.Payload));
    }

    private BoardAssignHandler Handler() => new(new TaskService(db.DataSource));

    private async Task<(Guid Project, Guid Card)> AddCardAsync()
    {
        var (gc, _) = await db.CreateGcWithMessageAsync();
        var project = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, project, "Card", TaskState.Backlog, null, TaskGate.None, gc, null, null), Ct);
        await tx.CommitAsync(Ct);
        return (project, id);
    }

    private async Task<Guid> AddAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'assignee', 'active')");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<long> InboxCountAsync(Guid agent)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM inbox_item WHERE agent_id = $1 AND kind = 'assignment'");
        cmd.Parameters.AddWithValue(agent);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<TaskRecord> ReadAsync(Guid card) => (await new TaskRepository(db.DataSource).GetAsync(card, Ct))!;

    [Fact]
    public async Task The_owner_assigns_a_card_and_the_agent_gets_one_inbox_item()
    {
        SkipWithoutDatabase();
        var (project, card) = await AddCardAsync();
        var agent = await AddAgentAsync();
        var before = (await ReadAsync(card)).Version;

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, agent, before)), Ct));

        Assert.Equal(KanbanAssignStatuses.Applied, reply.Status);
        Assert.Equal(before + 1, reply.Revision);
        Assert.Equal(agent, (await ReadAsync(card)).OwnerAgentId);
        Assert.Equal(1, await InboxCountAsync(agent));
    }

    [Fact]
    public async Task A_stale_revision_conflicts_and_changes_nothing()
    {
        SkipWithoutDatabase();
        var (project, card) = await AddCardAsync();
        var agent = await AddAgentAsync();
        var before = (await ReadAsync(card)).Version;

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, agent, before + 4)), Ct));

        Assert.Equal(KanbanAssignStatuses.Conflict, reply.Status);
        Assert.Equal(before, reply.Revision);
        Assert.Null((await ReadAsync(card)).OwnerAgentId);
        Assert.Equal(0, await InboxCountAsync(agent));
    }

    [Fact]
    public async Task Assigning_the_current_assignee_again_is_refused_and_writes_nothing()
    {
        SkipWithoutDatabase();
        var (project, card) = await AddCardAsync();
        var agent = await AddAgentAsync();
        var handler = Handler();
        var before = (await ReadAsync(card)).Version;
        await handler.HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, agent, before)), Ct);

        var reply = Decode(await handler.HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, agent, before + 1)), Ct));

        Assert.Equal(KanbanAssignStatuses.Refused, reply.Status);
        Assert.Equal(1, await InboxCountAsync(agent));
    }

    [Fact]
    public async Task Clearing_the_assignee_applies()
    {
        SkipWithoutDatabase();
        var (project, card) = await AddCardAsync();
        var agent = await AddAgentAsync();
        var handler = Handler();
        var before = (await ReadAsync(card)).Version;
        await handler.HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, agent, before)), Ct);

        var reply = Decode(await handler.HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, null, before + 1)), Ct));

        Assert.Equal(KanbanAssignStatuses.Applied, reply.Status);
        Assert.Null((await ReadAsync(card)).OwnerAgentId);
    }

    [Fact]
    public async Task Invalid_input_is_reported_as_invalid()
    {
        SkipWithoutDatabase();
        var (project, card) = await AddCardAsync();
        var agent = await AddAgentAsync();

        var badRevision = Decode(await Handler().HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, agent, 0)), Ct));
        var unknown = Decode(await Handler().HandleAsync(Request(_owner, new KanbanAssignRequest(project, Guid.CreateVersion7(), agent, 1)), Ct));
        var wrongProject = Decode(await Handler().HandleAsync(Request(_owner, new KanbanAssignRequest(Guid.CreateVersion7(), card, agent, 1)), Ct));
        var emptyAgent = Decode(await Handler().HandleAsync(Request(_owner, new KanbanAssignRequest(project, card, Guid.Empty, 1)), Ct));

        Assert.All([badRevision, unknown, wrongProject, emptyAgent], r => Assert.Equal(KanbanAssignStatuses.Invalid, r.Status));
        Assert.Null((await ReadAsync(card)).OwnerAgentId);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("human")]
    public async Task A_principal_that_is_not_the_owner_gets_not_found_and_nothing_changes(string kind)
    {
        SkipWithoutDatabase();
        var (project, card) = await AddCardAsync();
        var agent = await AddAgentAsync();
        var before = (await ReadAsync(card)).Version;
        var other = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), kind));

        var reply = await Handler().HandleAsync(Request(other, new KanbanAssignRequest(project, card, agent, before)), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Equal(before, (await ReadAsync(card)).Version);
        Assert.Equal(0, await InboxCountAsync(agent));
    }

    [Fact]
    public async Task A_payload_that_does_not_fit_the_schema_is_a_bad_request()
    {
        SkipWithoutDatabase();
        var context = new WireRequestContext(_owner, new Envelope(1, WireTypes.BoardAssign, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload("not an object")));

        var reply = await Handler().HandleAsync(context, Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }
}
