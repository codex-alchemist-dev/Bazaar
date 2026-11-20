using Bazaar.Client.Core.Wire;
using Bazaar.Client.RyuseiFuda.Phone;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Tests.Kanban.Phone;

/// <summary><see cref="WireKanbanBoardClient"/> against a requester that answers like the server's board handlers (doc 12).</summary>
public sealed class WireKanbanBoardClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _project = PhoneCards.ProjectId;
    private static readonly Guid _card = PhoneCards.Id(1);

    /// <summary>Records each request and answers it with a canned reply, or throws.</summary>
    private sealed class FakeRequester : IWireRequester
    {
        public List<(string Type, object Payload)> Sent { get; } = [];
        public object? Reply { get; set; }
        public Exception? Failure { get; set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload!));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult((TResponse)Reply!);
        }
    }

    private static WireRequestException WireFailure(string code) => new(new WireError(code, "nope", null));

    [Fact]
    public async Task Load_asks_for_the_snapshot_and_keeps_each_cards_revision()
    {
        var blocker = Guid.CreateVersion7();
        var requests = new FakeRequester
        {
            Reply = new KanbanBoardSnapshot(_project,
            [
                new KanbanBoardCard(_card, "Ship it", "ready", null, [blocker], ["Write the schema"], Revision: 4),
                new KanbanBoardCard(PhoneCards.Id(2), "Plan", "backlog", null, [], [], Revision: 1),
            ]),
        };

        var board = await new WireKanbanBoardClient(requests).LoadAsync(_project, Ct);

        var sent = Assert.Single(requests.Sent);
        Assert.Equal(WireTypes.BoardSnapshot, sent.Type);
        Assert.Equal(_project, ((KanbanBoardRequest)sent.Payload).ProjectId);
        Assert.Equal(2, board.Cards.Count);
        var card = board.Cards[0];
        Assert.Equal(4, card.Revision);
        Assert.True(card.Blocked);
        Assert.Equal(["Write the schema"], card.BlockerTitles);
    }

    [Fact]
    public async Task Move_sends_project_card_state_and_expected_revision()
    {
        var requests = new FakeRequester { Reply = new KanbanMoveReply(KanbanMoveStatuses.Applied, 5, []) };

        var result = await new WireKanbanBoardClient(requests).MoveAsync(_project, _card, "in_progress", 4, Ct);

        var sent = Assert.Single(requests.Sent);
        Assert.Equal(WireTypes.BoardMove, sent.Type);
        var request = (KanbanMoveRequest)sent.Payload;
        Assert.Equal(new KanbanMoveRequest(_project, _card, "in_progress", 4), request);
        Assert.Equal(new KanbanMoveResult.Applied(5), result);
    }

    [Fact]
    public async Task A_conflict_reply_carries_the_current_revision()
    {
        var requests = new FakeRequester { Reply = new KanbanMoveReply(KanbanMoveStatuses.Conflict, 9, []) };

        var result = await new WireKanbanBoardClient(requests).MoveAsync(_project, _card, "review", 4, Ct);

        Assert.Equal(new KanbanMoveResult.Conflict(9), result);
    }

    [Fact]
    public async Task A_refused_reply_carries_blocker_titles_and_the_reason()
    {
        var requests = new FakeRequester
        {
            Reply = new KanbanMoveReply(KanbanMoveStatuses.Refused, null, ["Write the schema", "Pick a name"], "Blocked."),
        };

        var result = await new WireKanbanBoardClient(requests).MoveAsync(_project, _card, "in_progress", 4, Ct);

        var refused = Assert.IsType<KanbanMoveResult.Refused>(result);
        Assert.Equal(["Write the schema", "Pick a name"], refused.BlockerTitles);
        Assert.Equal("Blocked.", refused.Reason);
    }

    [Fact]
    public async Task An_invalid_reply_carries_the_reason()
    {
        var requests = new FakeRequester { Reply = new KanbanMoveReply(KanbanMoveStatuses.Invalid, null, [], "No such card in this project.") };

        var result = await new WireKanbanBoardClient(requests).MoveAsync(_project, _card, "done", 4, Ct);

        Assert.Equal(new KanbanMoveResult.Invalid("No such card in this project."), result);
    }

    [Theory]
    [InlineData("mystery", 3L)]
    [InlineData("applied", null)]
    [InlineData("conflict", null)]
    public async Task A_reply_the_client_cannot_use_is_invalid_not_applied(string status, long? revision)
    {
        var requests = new FakeRequester { Reply = new KanbanMoveReply(status, revision, []) };

        var result = await new WireKanbanBoardClient(requests).MoveAsync(_project, _card, "done", 4, Ct);

        Assert.IsType<KanbanMoveResult.Invalid>(result);
    }

    [Fact]
    public async Task A_bad_request_error_is_invalid()
    {
        var requests = new FakeRequester { Failure = WireFailure(WireErrorCodes.BadRequest) };

        var result = await new WireKanbanBoardClient(requests).MoveAsync(_project, _card, "done", 4, Ct);

        Assert.Equal(new KanbanMoveResult.Invalid("nope"), result);
    }

    [Fact]
    public async Task Any_other_wire_error_or_a_lost_connection_reaches_the_caller()
    {
        var denied = new FakeRequester { Failure = WireFailure(WireErrorCodes.NotFound) };
        var dropped = new FakeRequester { Failure = new HttpRequestException("connection dropped") };

        await Assert.ThrowsAsync<WireRequestException>(() => new WireKanbanBoardClient(denied).MoveAsync(_project, _card, "done", 4, Ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => new WireKanbanBoardClient(dropped).MoveAsync(_project, _card, "done", 4, Ct));
    }

    [Fact]
    public async Task A_move_uses_the_revision_the_board_was_loaded_with()
    {
        var requests = new FakeRequester
        {
            Reply = new KanbanBoardSnapshot(_project, [new KanbanBoardCard(_card, "Ship it", "ready", null, [], [], Revision: 2)]),
        };
        var client = new WireKanbanBoardClient(requests);
        var board = await client.LoadAsync(_project, Ct);
        requests.Reply = new KanbanMoveReply(KanbanMoveStatuses.Conflict, 3, []);

        var result = await client.MoveAsync(_project, board.Cards[0].Id, "in_progress", board.Cards[0].Revision, Ct);

        Assert.Equal(new KanbanMoveResult.Conflict(3), result);
        Assert.Equal(2, ((KanbanMoveRequest)requests.Sent[1].Payload).ExpectedRevision);
    }

    [Fact]
    public async Task Two_moves_from_the_same_revision_apply_once_against_the_fake_board()
    {
        // The fake decides like the server's revision check, so the pane tests can rely on it.
        var fake = new FakeKanbanBoardClient(PhoneCards.Card(1, "Ship it", "ready", revision: 2));

        var results = await Task.WhenAll(
            fake.MoveAsync(_project, _card, "in_progress", 2, Ct),
            fake.MoveAsync(_project, _card, "backlog", 2, Ct));

        Assert.Single(results, r => r is KanbanMoveResult.Applied);
        Assert.Single(results, r => r is KanbanMoveResult.Conflict { CurrentRevision: 3 });
    }
}
