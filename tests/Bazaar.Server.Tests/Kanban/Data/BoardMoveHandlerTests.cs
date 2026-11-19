using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.RyuseiFuda.Kanban.Wire;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The <c>board.move</c> wire handler over the real task service (doc 12, Card revision and client move command).</summary>
public sealed class BoardMoveHandlerTests(KanbanDb db) : IClassFixture<KanbanDb>
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

    private static readonly FakeConnection _owner = new(new PrincipalRef(Guid.CreateVersion7(), "owner"));

    private static WireRequestContext Request(IWireConnection connection, KanbanMoveRequest move) =>
        new(connection, new Envelope(1, WireTypes.BoardMove, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload(move)));

    private static KanbanMoveReply Decode(WireReply reply)
    {
        Assert.True(reply.IsOk, reply.Error?.Message);
        return _codec.DecodePayload<KanbanMoveReply>(new Envelope(1, WireTypes.BoardMove, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, reply.Payload));
    }

    private BoardMoveHandler Handler() => new(new TaskService(db.DataSource));

    private async Task<Guid> AddTaskAsync(Guid project, Guid gc, string title, TaskState state)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, project, title, state, null, TaskGate.None, gc, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private async Task<(Guid Project, Guid Gc)> NewBoardAsync()
    {
        var (gc, _) = await db.CreateGcWithMessageAsync();
        return (Guid.CreateVersion7(), gc);
    }

    private static async Task<long> RevisionOfAsync(TaskService service, Guid project, Guid card) =>
        (await service.GetBoardSnapshotAsync(project, Ct)).Cards.Single(c => c.Id == card).Revision;

    [Fact]
    public async Task The_snapshot_carries_each_cards_revision_and_a_move_bumps_it()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Ready);
        var service = new TaskService(db.DataSource);
        var before = await RevisionOfAsync(service, project, card);
        Assert.True(before >= 1);

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, card, "in_progress", before)), Ct));

        Assert.Equal(KanbanMoveStatuses.Applied, reply.Status);
        Assert.Equal(before + 1, reply.Revision);
        Assert.Equal(reply.Revision, await RevisionOfAsync(service, project, card));
        Assert.Equal("in_progress", (await service.GetBoardSnapshotAsync(project, Ct)).Cards.Single(c => c.Id == card).State);
    }

    [Fact]
    public async Task A_stale_revision_conflicts_with_the_current_one_and_changes_nothing()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Ready);
        var service = new TaskService(db.DataSource);
        var before = await RevisionOfAsync(service, project, card);

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, card, "in_progress", before + 5)), Ct));

        Assert.Equal(KanbanMoveStatuses.Conflict, reply.Status);
        Assert.Equal(before, reply.Revision);
        Assert.Equal("ready", (await service.GetBoardSnapshotAsync(project, Ct)).Cards.Single(c => c.Id == card).State);
        Assert.Equal(before, await RevisionOfAsync(service, project, card));
    }

    [Fact]
    public async Task A_blocked_card_is_refused_with_the_blocker_titles()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var blocker = await AddTaskAsync(project, gc, "Write the schema", TaskState.Backlog);
        var blocked = await AddTaskAsync(project, gc, "Wire the API", TaskState.Ready);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(blocked, blocker, new TaskActor(TaskActorKind.Owner, _owner.Principal.Id), Ct);
        var before = await RevisionOfAsync(service, project, blocked);

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, blocked, "in_progress", before)), Ct));

        Assert.Equal(KanbanMoveStatuses.Refused, reply.Status);
        Assert.Equal(["Write the schema"], reply.BlockerTitles);
        Assert.False(string.IsNullOrWhiteSpace(reply.Reason));
        Assert.Equal("ready", (await service.GetBoardSnapshotAsync(project, Ct)).Cards.Single(c => c.Id == blocked).State);
    }

    [Fact]
    public async Task Review_to_done_for_a_pr_tied_card_is_refused_because_done_means_merged()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Fix the build", TaskState.Review);
        var service = new TaskService(db.DataSource);
        await service.AddLinkAsync(card, TaskLinkKind.Pr, "owner-x/repo#42", new TaskActor(TaskActorKind.Owner, _owner.Principal.Id), Ct);
        var before = await RevisionOfAsync(service, project, card);

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, card, "done", before)), Ct));

        Assert.Equal(KanbanMoveStatuses.Refused, reply.Status);
        Assert.Contains("merged", reply.Reason);
        Assert.Equal("review", (await service.GetBoardSnapshotAsync(project, Ct)).Cards.Single(c => c.Id == card).State);
    }

    [Theory]
    [InlineData("sideways", 1, "target state")]
    [InlineData("done", 0, "revision")]
    [InlineData("done", -3, "revision")]
    public async Task Invalid_input_is_reported_as_invalid_without_touching_the_card(string toState, long revision, string reasonPart)
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Ready);

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, card, toState, revision)), Ct));

        Assert.Equal(KanbanMoveStatuses.Invalid, reply.Status);
        Assert.Contains(reasonPart, reply.Reason);
    }

    [Fact]
    public async Task An_unknown_card_or_a_card_from_another_project_is_invalid()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Ready);

        var unknown = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, Guid.CreateVersion7(), "in_progress", 1)), Ct));
        var wrongProject = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(Guid.CreateVersion7(), card, "in_progress", 1)), Ct));
        var emptyIds = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(Guid.Empty, Guid.Empty, "in_progress", 1)), Ct));

        Assert.Equal(KanbanMoveStatuses.Invalid, unknown.Status);
        Assert.Equal(KanbanMoveStatuses.Invalid, wrongProject.Status);
        Assert.Equal(KanbanMoveStatuses.Invalid, emptyIds.Status);
    }

    [Fact]
    public async Task A_move_the_table_does_not_list_is_invalid()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Backlog);
        var service = new TaskService(db.DataSource);
        var before = await RevisionOfAsync(service, project, card);

        var reply = Decode(await Handler().HandleAsync(Request(_owner, new KanbanMoveRequest(project, card, "done", before)), Ct));

        Assert.Equal(KanbanMoveStatuses.Invalid, reply.Status);
        Assert.Equal(before, await RevisionOfAsync(service, project, card));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("human")]
    public async Task A_principal_that_is_not_the_owner_gets_not_found_and_nothing_moves(string kind)
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Ready);
        var service = new TaskService(db.DataSource);
        var before = await RevisionOfAsync(service, project, card);
        var other = new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), kind));

        var reply = await Handler().HandleAsync(Request(other, new KanbanMoveRequest(project, card, "in_progress", before)), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Equal(before, await RevisionOfAsync(service, project, card));
    }

    [Fact]
    public async Task A_payload_that_does_not_fit_the_schema_is_a_bad_request()
    {
        SkipWithoutDatabase();
        var context = new WireRequestContext(_owner, new Envelope(1, WireTypes.BoardMove, EnvelopeKind.Request, "req-1", null, null, null, null,
            _codec.EncodePayload("not an object")));

        var reply = await Handler().HandleAsync(context, Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task Two_moves_with_the_same_expected_revision_apply_exactly_once()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var card = await AddTaskAsync(project, gc, "Ship it", TaskState.Ready);
        var service = new TaskService(db.DataSource);
        var before = await RevisionOfAsync(service, project, card);
        var handler = Handler();

        // Ready can go to in_progress or back to backlog (the second needs a reason). Both race on the same revision.
        var replies = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
                Decode(await handler.HandleAsync(Request(_owner,
                    new KanbanMoveRequest(project, card, i % 2 == 0 ? "in_progress" : "backlog", before, "reshuffle")), Ct)), Ct)));

        var applied = replies.Where(r => r.Status == KanbanMoveStatuses.Applied).ToList();
        Assert.Single(applied);
        Assert.Equal(before + 1, applied[0].Revision);
        var losers = replies.Where(r => r.Status != KanbanMoveStatuses.Applied).ToList();
        Assert.Equal(5, losers.Count);
        Assert.All(losers, r =>
        {
            Assert.Equal(KanbanMoveStatuses.Conflict, r.Status);
            Assert.Equal(before + 1, r.Revision);
        });
        Assert.Equal(before + 1, await RevisionOfAsync(service, project, card));
    }
}
