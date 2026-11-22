namespace Bazaar.Server.Portability;

/// <summary>
/// Doc 44 section 9 adapter over <see cref="ObsidianImporter"/>. Detect and Parse are delegated unchanged. Map is the
/// shared <see cref="DefaultMapping"/>, which has no format-specific rules yet.
/// </summary>
public sealed class ObsidianMappingImporter : IMappingImporter
{
    private const string _formatId = "obsidian.md";
    private readonly ObsidianImporter _inner = new();

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

    /// <summary>One note with a wiki-link, a tag and a heading.</summary>
    internal static ImportSource BuiltInFixture() => new(
        "fixture-note.md",
        System.Text.Encoding.UTF8.GetBytes("# Fixture note\nSee [[Other note]] for context. #idea\n"));
}
