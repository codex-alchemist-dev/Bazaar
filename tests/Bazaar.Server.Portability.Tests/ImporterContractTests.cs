using System.Text;

namespace Bazaar.Server.Portability.Tests;

public class ImporterContractTests
{
    private sealed class FakeImporter(bool impure = false, bool undeclared = false) : IMappingImporter
    {
        private int _calls;

        public string Id => "fake";
        public string Version => "1.0.0";
        public string FormatId => "fake.txt";
        public ImporterDeclaration Declaration { get; } = new("fake.txt", [ImportTargetKind.VaultFile]);
        public DetectResult Detect(ImportSource source) => new(1, "x");
        public IReadOnlyList<SourceRecord> Parse(ImportSource source) =>
            [new SourceRecord("k", undeclared ? ImportCommitter.GcEntryKind : "document", "t", "b", new Dictionary<string, string>())];
        public ImporterSelfTestResult SelfTest() => new(true);

        public MappingResult Map(SourceRecord record, MappingRules rules) =>
            impure && ++_calls % 2 == 0 ? MappingResult.Skip("flip") : DefaultMapping.Map(record);
    }

    private static readonly ImportSource _fixture = new("f.txt", Encoding.UTF8.GetBytes("x"));

    [Fact]
    public void A_well_behaved_importer_passes() =>
        Assert.Empty(ImporterContractChecks.Check(new FakeImporter(), _fixture));

    [Fact]
    public void Impure_mapping_is_reported() =>
        Assert.Contains(ImporterContractChecks.Check(new FakeImporter(impure: true), _fixture), p => p.Contains("not deterministic"));

    [Fact]
    public void Undeclared_target_is_reported() =>
        Assert.Contains(ImporterContractChecks.Check(new FakeImporter(undeclared: true), _fixture), p => p.Contains("undeclared target"));

    [Fact]
    public void Rules_hash_changes_with_version_and_options_but_not_option_order()
    {
        var a = new MappingRules("1.0.0", new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" });
        var b = new MappingRules("1.0.0", new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" });
        var c = new MappingRules("1.1.0", a.Options);
        var d = new MappingRules("1.0.0", new Dictionary<string, string> { ["x"] = "9", ["y"] = "2" });
        Assert.Equal(a.Hash(), b.Hash());
        Assert.NotEqual(a.Hash(), c.Hash());
        Assert.NotEqual(a.Hash(), d.Hash());
    }

    [Fact]
    public void Importers_read_only_the_staging_input() =>
        Assert.Equal(["staging-input"], new FakeImporter().Declaration.Reads);
}
