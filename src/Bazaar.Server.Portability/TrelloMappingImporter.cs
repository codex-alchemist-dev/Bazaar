namespace Bazaar.Server.Portability;

/// <summary>
/// Doc 44 section 9 adapter over <see cref="TrelloImporter"/>. Detect and Parse are delegated unchanged. Map adds the
/// Trello list as a Kanban column attribute on each card: the five doc 44 columns match by name (case-insensitive),
/// and any other list name goes to Backlog with a <c>column_warning</c> attribute for the preview. Each card becomes a
/// Kanban task through the <see cref="ImportTargetKind.KanbanTask"/> target.
/// </summary>
public sealed class TrelloMappingImporter : IMappingImporter
{
    private const string _formatId = "trello.json";
    private readonly TrelloImporter _inner = new();

    public string Id => _inner.Id;
    public string Version => _inner.Version;
    public string FormatId => _formatId;
    public ImporterDeclaration Declaration { get; } = new(_formatId, [ImportTargetKind.KanbanTask]);

    public DetectResult Detect(ImportSource source) => _inner.Detect(source);

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => _inner.Parse(source);

    /// <summary>The Kanban columns of doc 44 section 8.2, in board order.</summary>
    public static IReadOnlyList<string> KanbanColumns { get; } = ["Backlog", "Ready", "In progress", "Review", "Done"];

    public MappingResult Map(SourceRecord record, MappingRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != "card") return DefaultMapping.Map(record);

        var list = record.Attributes.TryGetValue("list", out var listName) ? listName : "";
        var column = KanbanColumns.FirstOrDefault(c => string.Equals(c, list.Trim(), StringComparison.OrdinalIgnoreCase));

        var attributes = new Dictionary<string, string>(record.Attributes, StringComparer.Ordinal)
        {
            ["kanban_column"] = column ?? "Backlog",
        };
        if (column is null) attributes["column_warning"] = "unknown list: " + list;

        var mapped = record with { Kind = ImportCommitter.KanbanTaskKind, Attributes = attributes };
        return MappingResult.To(ImportTargetKind.KanbanTask, mapped);
    }

    public ImporterSelfTestResult SelfTest()
    {
        try
        {
            var fixture = BuiltInFixture();
            if (Detect(fixture).Confidence <= 0) return new ImporterSelfTestResult(false, "fixture not detected");
            return Parse(fixture).Count > 0
                ? new ImporterSelfTestResult(true)
                : new ImporterSelfTestResult(false, "fixture parsed to no records");
        }
        catch (Exception ex)
        {
            return new ImporterSelfTestResult(false, ex.Message);
        }
    }

    /// <summary>A two-card board export, used by the self-test and the contract tests.</summary>
    internal static ImportSource BuiltInFixture() => new(
        "board.json",
        System.Text.Encoding.UTF8.GetBytes("""
            {
              "name": "Fixture board",
              "lists": [ { "id": "l1", "name": "Ready" } ],
              "cards": [
                { "id": "c1", "name": "Write spec", "desc": "Draft the spec.", "idList": "l1", "closed": false, "due": null, "labels": [] },
                { "id": "c2", "name": "Ship it", "desc": "", "idList": "l1", "closed": true, "due": null, "labels": [] }
              ]
            }
            """));
}
