namespace Bazaar.Server.Portability;

/// <summary>
/// Doc 44 section 9 adapter over <see cref="TrelloImporter"/>. Detect and Parse are delegated unchanged. Map is the
/// shared <see cref="DefaultMapping"/>, which has no format-specific rules yet.
/// </summary>
public sealed class TrelloMappingImporter : IMappingImporter
{
    private const string _formatId = "trello.json";
    private readonly TrelloImporter _inner = new();

    public string Id => _inner.Id;
    public string Version => _inner.Version;
    public string FormatId => _formatId;
    public ImporterDeclaration Declaration { get; } = new(_formatId, [ImportTargetKind.VaultFile]);

    public DetectResult Detect(ImportSource source) => _inner.Detect(source);

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => _inner.Parse(source);

    public MappingResult Map(SourceRecord record, MappingRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return DefaultMapping.Map(record);
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
