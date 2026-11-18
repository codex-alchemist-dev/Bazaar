using System.Text;

namespace Bazaar.Server.Portability.Tests;

public class TrelloImporterTests
{
    private static string BoardJson() => """
        {
          "name": "Launch board",
          "lists": [
            { "id": "l1", "name": "Ready", "closed": false, "pos": 1 },
            { "id": "l2", "name": "Done", "closed": false, "pos": 2 }
          ],
          "cards": [
            { "id": "c1", "name": "Write spec", "desc": "Draft the spec.", "idList": "l1",
              "closed": false, "due": "2026-11-20T10:00:00.000Z",
              "labels": [ { "name": "design" }, { "name": "urgent" } ] },
            { "id": "c2", "name": "Ship it", "desc": "", "idList": "l2",
              "closed": true, "due": null, "labels": [] },
            { "id": "c3", "name": "Orphan", "desc": "No list.", "idList": "gone",
              "closed": false, "due": null, "labels": [] }
          ],
          "checklists": []
        }
        """;

    private static ImportSource Src(string text) => new("board.json", Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Detects_trello_export_with_high_confidence()
    {
        Assert.Equal(0.95, new TrelloImporter().Detect(Src(BoardJson())).Confidence);
    }

    [Fact]
    public void Rejects_non_trello_json_and_text()
    {
        var importer = new TrelloImporter();
        Assert.Equal(0, importer.Detect(Src("[1, 2, 3]")).Confidence);
        Assert.Equal(0, importer.Detect(Src("""{ "lists": [], "cards": [] }""")).Confidence);
        Assert.Equal(0, importer.Detect(Src("""{ "name": "x", "lists": {}, "cards": [] }""")).Confidence);
        Assert.Equal(0, importer.Detect(Src("""{ "name": "x", "lists": [""")).Confidence);
        Assert.Equal(0, importer.Detect(Src("just some notes")).Confidence);
        Assert.Equal(0, importer.Detect(new ImportSource("empty.json", [])).Confidence);
    }

    [Fact]
    public void Parses_one_card_record_per_card_with_list_and_board_attributes()
    {
        var records = new TrelloImporter().Parse(Src(BoardJson()));
        Assert.Equal(3, records.Count);

        var first = records[0];
        Assert.Equal("card", first.Kind);
        Assert.Equal("Write spec", first.Title);
        Assert.Equal("trello", first.Attributes["format"]);
        Assert.Equal("Ready", first.Attributes["list"]);
        Assert.Equal("Launch board", first.Attributes["board"]);
        Assert.Equal("board.json", first.Attributes["source"]);
        Assert.Equal("design,urgent", first.Attributes["labels"]);
        Assert.NotEqual(records[1].SourceKey, first.SourceKey);
    }

    [Fact]
    public void Resolves_list_name_from_id_and_writes_it_as_leading_body_line()
    {
        var records = new TrelloImporter().Parse(Src(BoardJson()));
        Assert.Equal("Done", records[1].Attributes["list"]);
        Assert.Equal("List: Done", records[1].Body);
    }

    [Fact]
    public void Writes_due_line_before_description()
    {
        var first = new TrelloImporter().Parse(Src(BoardJson()))[0];
        Assert.Equal("List: Ready\nDue: 2026-11-20T10:00:00.000Z\nDraft the spec.", first.Body);
    }

    [Fact]
    public void Omits_due_line_when_due_is_null()
    {
        var card = new TrelloImporter().Parse(Src(BoardJson()))[1];
        Assert.Equal("List: Done", card.Body);
        Assert.DoesNotContain("Due:", card.Body);
        Assert.False(card.Attributes.ContainsKey("labels"));
    }

    [Fact]
    public void Records_closed_flag_as_attribute()
    {
        var records = new TrelloImporter().Parse(Src(BoardJson()));
        Assert.Equal("false", records[0].Attributes["closed"]);
        Assert.Equal("true", records[1].Attributes["closed"]);
    }

    [Fact]
    public void Card_with_unknown_list_gets_unknown_list_name()
    {
        var orphan = new TrelloImporter().Parse(Src(BoardJson()))[2];
        Assert.Equal("unknown", orphan.Attributes["list"]);
        Assert.StartsWith("List: unknown\n", orphan.Body);
        Assert.Equal("Orphan", orphan.Title);
    }

    [Fact]
    public void Source_key_is_stable_for_same_card_and_content()
    {
        var importer = new TrelloImporter();
        var a = importer.Parse(Src(BoardJson()))[0].SourceKey;
        var b = importer.Parse(Src(BoardJson()))[0].SourceKey;
        Assert.Equal(a, b);
    }
}
