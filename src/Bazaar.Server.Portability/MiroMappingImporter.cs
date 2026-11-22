namespace Bazaar.Server.Portability;

/// <summary>
/// Doc 44 section 9 adapter over <see cref="MiroImporter"/>. Detect and Parse are delegated unchanged. Map is the
/// shared <see cref="DefaultMapping"/>, which has no format-specific rules yet.
/// </summary>
public sealed class MiroMappingImporter : IMappingImporter
{
    private const string _formatId = "miro.csv";
    private readonly MiroImporter _inner = new();

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

    /// <summary>A two-row CSV export with a titled row and an untitled row. The layout is modelled (see MiroImporter).</summary>
    internal static ImportSource BuiltInFixture() => new(
        "miro-board.csv",
        System.Text.Encoding.UTF8.GetBytes("Type,Title,Text\nsticky_note,Idea,Ship the importer\nsticky_note,,Second note\n"));
}
