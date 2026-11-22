namespace Bazaar.Server.Portability.Tests;

public class TrelloMappingImporterTests
{
    [Fact]
    public void Format_id_and_declaration_read_only_the_staging_input()
    {
        var importer = new TrelloMappingImporter();
        Assert.Equal("trello.json", importer.FormatId);
        Assert.Equal(["staging-input"], importer.Declaration.Reads);
        Assert.Equal([ImportTargetKind.KanbanTask], importer.Declaration.Targets);
    }

    [Fact]
    public void Id_and_version_come_from_the_wrapped_importer()
    {
        var importer = new TrelloMappingImporter();
        Assert.Equal(new TrelloImporter().Id, importer.Id);
        Assert.Equal(new TrelloImporter().Version, importer.Version);
    }

    [Fact]
    public void Detect_and_parse_match_the_wrapped_importer()
    {
        var fixture = TrelloMappingImporter.BuiltInFixture();
        var wrapped = new TrelloImporter();
        var adapter = new TrelloMappingImporter();

        Assert.Equal(wrapped.Detect(fixture), adapter.Detect(fixture));
        var expected = wrapped.Parse(fixture);
        var actual = adapter.Parse(fixture);
        Assert.Equal(expected.Select(r => (r.SourceKey, r.Kind, r.Title, r.Body)), actual.Select(r => (r.SourceKey, r.Kind, r.Title, r.Body)));
    }

    [Fact]
    public void Map_proposes_each_card_as_a_kanban_task_and_keeps_its_content()
    {
        var importer = new TrelloMappingImporter();
        var records = importer.Parse(TrelloMappingImporter.BuiltInFixture());
        Assert.NotEmpty(records);

        foreach (var record in records)
        {
            var result = importer.Map(record, MappingRules.Default);
            Assert.False(result.IsSkipped);
            Assert.Equal(ImportTargetKind.KanbanTask, result.Target);
            Assert.NotNull(result.Record);
            Assert.Equal(ImportCommitter.KanbanTaskKind, result.Record!.Kind);
            Assert.Equal(record.SourceKey, result.Record.SourceKey);
            Assert.Equal(record.Title, result.Record.Title);
            Assert.Equal(record.Body, result.Record.Body);
            Assert.Equal(record.Attributes["list"], result.Record.Attributes["list"]);
        }
    }

    [Theory]
    [InlineData("Ready", "Ready")]
    [InlineData("in progress", "In progress")]
    [InlineData("  Review ", "Review")]
    [InlineData("DONE", "Done")]
    [InlineData("Backlog", "Backlog")]
    public void Map_puts_a_card_in_the_kanban_column_with_the_same_name(string list, string expected)
    {
        var mapped = MapCardInList(list);
        Assert.Equal(expected, mapped.Attributes["kanban_column"]);
        Assert.False(mapped.Attributes.ContainsKey("column_warning"));
    }

    [Fact]
    public void Map_sends_an_unknown_list_to_backlog_with_a_warning()
    {
        var mapped = MapCardInList("Doing");
        Assert.Equal("Backlog", mapped.Attributes["kanban_column"]);
        Assert.Equal("unknown list: Doing", mapped.Attributes["column_warning"]);
    }

    [Fact]
    public void Map_keeps_the_list_attribute_and_the_closed_flag_on_the_card()
    {
        var importer = new TrelloMappingImporter();
        var closed = importer.Parse(TrelloMappingImporter.BuiltInFixture()).Single(r => r.Attributes["closed"] == "true");
        var mapped = importer.Map(closed, MappingRules.Default).Record!;
        Assert.Equal("true", mapped.Attributes["closed"]);
        Assert.Equal(closed.Attributes["list"], mapped.Attributes["list"]);
    }

    [Fact]
    public void Map_is_deterministic_for_the_same_record_and_rules()
    {
        var importer = new TrelloMappingImporter();
        var record = importer.Parse(TrelloMappingImporter.BuiltInFixture())[0];
        Assert.True(ImporterContractChecks.SameMapping(
            importer.Map(record, MappingRules.Default),
            importer.Map(record, MappingRules.Default)));
    }

    [Fact]
    public void Map_does_not_change_the_source_record()
    {
        var importer = new TrelloMappingImporter();
        var record = importer.Parse(TrelloMappingImporter.BuiltInFixture())[0];
        _ = importer.Map(record, MappingRules.Default);
        Assert.False(record.Attributes.ContainsKey("kanban_column"));
    }

    private static SourceRecord MapCardInList(string list)
    {
        var importer = new TrelloMappingImporter();
        var card = importer.Parse(new ImportSource("board.json", System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "name": "Board",
              "lists": [ { "id": "l1", "name": "{{list}}" } ],
              "cards": [ { "id": "c1", "name": "Card", "desc": "", "idList": "l1", "closed": false, "due": null, "labels": [] } ]
            }
            """)))[0];
        return importer.Map(card, MappingRules.Default).Record!;
    }

    [Fact]
    public void Map_rejects_null_rules()
    {
        var importer = new TrelloMappingImporter();
        var record = importer.Parse(TrelloMappingImporter.BuiltInFixture())[0];
        Assert.Throws<ArgumentNullException>(() => importer.Map(record, null!));
    }

    [Fact]
    public void Self_test_passes()
    {
        var result = new TrelloMappingImporter().SelfTest();
        Assert.True(result.Ok);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Contract_checks_pass_on_the_built_in_fixture() =>
        Assert.Empty(ImporterContractChecks.Check(new TrelloMappingImporter(), TrelloMappingImporter.BuiltInFixture()));
}
