using System.Text.Json;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Wire;

/// <summary>The card revision and <c>board.move</c> payloads (doc 12, Card revision and client move command).</summary>
public class KanbanWireTests
{
    private static readonly Guid _project = Guid.Parse("0196a6f0-0000-7000-8000-000000000a01");
    private static readonly Guid _card = Guid.Parse("0196a6f0-0000-7000-8000-000000000a02");

    [Fact]
    public void A_card_from_a_server_without_the_revision_field_reads_as_revision_zero()
    {
        var json = $$"""{"id":"{{_card}}","title":"Ship it","state":"ready","unfinishedBlockerIds":[],"blockerTitles":[]}""";

        var card = JsonSerializer.Deserialize(json, WireJsonContext.Default.KanbanBoardCard);

        Assert.NotNull(card);
        Assert.Equal(0, card.Revision);
        Assert.Equal("Ship it", card.Title);
    }

    [Fact]
    public void A_card_revision_is_written_as_revision_and_round_trips()
    {
        var card = new KanbanBoardCard(_card, "Ship it", "ready", null, [], [], Revision: 7);

        var json = JsonSerializer.Serialize(new KanbanBoardSnapshot(_project, [card]), WireJsonContext.Default.KanbanBoardSnapshot);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.KanbanBoardSnapshot);

        Assert.Contains("\"revision\":7", json);
        Assert.Equal(7, Assert.Single(back!.Cards).Revision);
    }

    [Fact]
    public void A_move_request_uses_camel_case_names_and_leaves_out_a_missing_reason()
    {
        var json = JsonSerializer.Serialize(new KanbanMoveRequest(_project, _card, "in_progress", 3), WireJsonContext.Default.KanbanMoveRequest);

        Assert.Equal($$"""{"projectId":"{{_project}}","cardId":"{{_card}}","toState":"in_progress","expectedRevision":3}""", json);
        Assert.Equal(WireTypes.BoardMove, "board.move");
    }

    [Fact]
    public void A_move_reply_round_trips_with_unknown_fields_ignored()
    {
        var reply = new KanbanMoveReply(KanbanMoveStatuses.Refused, null, ["Write the schema"], "Blocked.");
        var json = JsonSerializer.Serialize(reply, WireJsonContext.Default.KanbanMoveReply);
        var widened = json.Replace("{", "{\"later\":1,", StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize(widened, WireJsonContext.Default.KanbanMoveReply);

        Assert.Equal("""{"status":"refused","blockerTitles":["Write the schema"],"reason":"Blocked."}""", json);
        Assert.Equal(reply.Status, back!.Status);
        Assert.Equal(reply.BlockerTitles, back.BlockerTitles);
        Assert.Equal(reply.Reason, back.Reason);
        Assert.Null(back.Revision);
    }
}
