using System.Text;

namespace Bazaar.Portability.Tests;

public class ImportPipelineTests
{
    private static readonly IReadOnlySet<string> _noKeys = new HashSet<string>(StringComparer.Ordinal);

    private static ImportSource Src(string text) => new("notes.txt", Encoding.UTF8.GetBytes(text));

    private static SourceRecord Record(string key) =>
        new(key, "document", key, "body", new Dictionary<string, string>());

    private sealed class FakeImporter(string id, double confidence, Func<ImportSource, IReadOnlyList<SourceRecord>>? parse = null, string version = "1.0.0") : IPortabilityImporter
    {
        public string Id => id;
        public string Version => version;
        public DetectResult Detect(ImportSource source) => new(confidence, id + " reason");
        public IReadOnlyList<SourceRecord> Parse(ImportSource source) =>
            parse is null ? [Record(id + "-1")] : parse(source);
    }

    [Fact]
    public void One_clear_importer_is_ready_with_preview_and_hash()
    {
        var pipeline = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9, _ => [Record("k1"), Record("k2")])]);

        var result = pipeline.Run(Src("hello"), _noKeys);

        Assert.Equal(ImportStatus.Ready, result.Status);
        Assert.Equal("bazaar.import.a", result.ImporterId);
        Assert.Single(result.Candidates);
        Assert.Equal(2, result.Preview!.New.Count);
        Assert.Empty(result.Preview.Duplicates);
        Assert.Matches("^[0-9a-f]{64}$", result.MappingHash);
    }

    [Fact]
    public void Committed_keys_are_reported_as_duplicates()
    {
        var pipeline = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9, _ => [Record("k1"), Record("k2")])]);

        var result = pipeline.Run(Src("hello"), new HashSet<string>(["k1"], StringComparer.Ordinal));

        Assert.Equal(ImportStatus.Ready, result.Status);
        Assert.Equal("k2", Assert.Single(result.Preview!.New).SourceKey);
        Assert.Equal("k1", Assert.Single(result.Preview.Duplicates).SourceKey);
    }

    [Fact]
    public void Candidates_within_point_one_are_ambiguous()
    {
        var pipeline = new ImportPipeline([
            new FakeImporter("bazaar.import.a", 0.8),
            new FakeImporter("bazaar.import.b", 0.75)]);

        var result = pipeline.Run(Src("hello"), _noKeys);

        Assert.Equal(ImportStatus.Ambiguous, result.Status);
        Assert.Null(result.ImporterId);
        Assert.Null(result.Preview);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("bazaar.import.a", result.Candidates[0].Id);
    }

    [Fact]
    public void Unrecognised_for_binary_input()
    {
        var pipeline = new ImportPipeline([new PlainTextImporter()]);
        var source = new ImportSource("blob.bin", [0x50, 0x4B, 0x00, 0x03]);

        var result = pipeline.Run(source, _noKeys);

        Assert.Equal(ImportStatus.Unrecognised, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Preview);
    }

    [Fact]
    public void Parse_exception_gives_failed_status_without_throwing()
    {
        var pipeline = new ImportPipeline([new FakeImporter("bazaar.import.bad", 0.9, _ => throw new InvalidDataException("corrupt record"))]);

        var result = pipeline.Run(Src("hello"), _noKeys);

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Equal("bazaar.import.bad", result.ImporterId);
        Assert.Equal("corrupt record", result.Message);
        Assert.Null(result.Preview);
        Assert.Equal(string.Empty, result.MappingHash);
    }

    [Fact]
    public void Preference_overrides_ambiguity()
    {
        var pipeline = new ImportPipeline([
            new FakeImporter("bazaar.import.a", 0.8),
            new FakeImporter("bazaar.import.b", 0.75)]);

        var result = pipeline.Run(Src("hello"), _noKeys, "bazaar.import.b");

        Assert.Equal(ImportStatus.Ready, result.Status);
        Assert.Equal("bazaar.import.b", result.ImporterId);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Unregistered_preference_is_unrecognised()
    {
        var pipeline = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9)]);

        var result = pipeline.Run(Src("hello"), _noKeys, "bazaar.import.missing");

        Assert.Equal(ImportStatus.Unrecognised, result.Status);
        Assert.Contains("bazaar.import.missing", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_is_refused_on_mapping_hash_mismatch()
    {
        var pipeline = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9)]);
        var dryRun = pipeline.Run(Src("hello"), _noKeys);

        Assert.True(ImportPipeline.CanCommit(dryRun, dryRun.MappingHash));
        Assert.False(ImportPipeline.CanCommit(dryRun, new string('0', 64)));
    }

    [Fact]
    public void Commit_is_refused_when_dry_run_is_not_ready()
    {
        var pipeline = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9, _ => throw new InvalidOperationException("boom"))]);
        var dryRun = pipeline.Run(Src("hello"), _noKeys);

        Assert.False(ImportPipeline.CanCommit(dryRun, dryRun.MappingHash));
    }

    [Fact]
    public void Mapping_hash_changes_with_importer_version()
    {
        var a = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9, version: "1.0.0")]).Run(Src("x"), _noKeys);
        var b = new ImportPipeline([new FakeImporter("bazaar.import.a", 0.9, version: "1.0.1")]).Run(Src("x"), _noKeys);

        Assert.NotEqual(a.MappingHash, b.MappingHash);
    }
}
