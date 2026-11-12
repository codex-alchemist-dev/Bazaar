using System.Collections.Immutable;
using Bazaar.RyuseiFuda.Roadmap;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>Front-matter rows become read-model inputs; bad documents are reported, not hidden (doc 38). Pure: no database.</summary>
public sealed class RoadmapDocumentLoaderTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _noStates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private static RoadmapDocumentRow Row(string path, string frontMatterJson, string title = "T") => new(path, title, frontMatterJson);

    [Fact]
    public void Valid_objective_and_phase_are_classified_and_slugged_from_the_file_name()
    {
        var load = RoadmapDocumentLoader.Classify(
        [
            Row("roadmap/phases/one.md", """{"kind":"phase","status":"active"}"""),
            Row("roadmap/objectives/a.md", """{"kind":"objective","phase":"one","status":"planned"}"""),
        ], _noStates);

        Assert.Empty(load.Errors);
        Assert.Equal(2, load.Documents.Count);
        var phase = Assert.Single(load.Documents, d => d.Document is RoadmapPhase);
        Assert.Equal("one", phase.Slug);
        var objective = Assert.Single(load.Documents, d => d.Document is RoadmapObjective);
        Assert.Null(objective.Slug);
    }

    [Fact]
    public void A_document_that_breaks_the_contract_is_reported_with_its_field_and_left_out()
    {
        var load = RoadmapDocumentLoader.Classify(
        [
            Row("roadmap/objectives/bad.md", """{"kind":"objective","phase":"one","status":"maybe"}"""),
            Row("roadmap/objectives/good.md", """{"kind":"objective","phase":"one","status":"done"}"""),
        ], _noStates);

        Assert.Single(load.Documents);
        var error = Assert.Single(load.Errors);
        Assert.Equal("roadmap/objectives/bad.md", error.Path);
        Assert.Equal("status", error.Field);
    }

    [Fact]
    public void A_repeated_phase_slug_keeps_the_first_by_path_and_reports_the_second()
    {
        var load = RoadmapDocumentLoader.Classify(
        [
            Row("roadmap/phases/one.md", """{"kind":"phase","status":"active"}"""),
            Row("archive/phases/one.md", """{"kind":"phase","status":"done"}"""),
        ], _noStates);

        Assert.Single(load.Documents);
        Assert.Equal("archive/phases/one.md", load.Documents[0].Path);
        var error = Assert.Single(load.Errors);
        Assert.Equal("roadmap/phases/one.md", error.Path);
        Assert.Equal("slug", error.Field);
    }

    [Fact]
    public void Front_matter_that_is_not_a_json_object_is_reported()
    {
        var load = RoadmapDocumentLoader.Classify([Row("roadmap/objectives/x.md", "[1,2]")], _noStates);

        Assert.Empty(load.Documents);
        Assert.Equal("frontmatter", Assert.Single(load.Errors).Field);
    }

    [Theory]
    [InlineData("roadmap/phases/one.md", "one")]
    [InlineData("one.md", "one")]
    [InlineData("roadmap/phases/one.txt", null)]
    [InlineData("roadmap/phases/.md", null)]
    public void Slug_is_the_file_name_without_the_md_suffix(string path, string? expected) =>
        Assert.Equal(expected, RoadmapDocumentLoader.SlugOf(path));
}
