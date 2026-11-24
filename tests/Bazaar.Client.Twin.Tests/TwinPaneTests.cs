using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Twin.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Twin.Tests;

public class TwinPaneTests
{
    private const string _model = """
        {"projects":[
          {"projectId":"bazaar","title":"Bazaar","facts":[
            {"factId":"f1","kind":"dependency","subject":"Npgsql","value":"8.0.0","confidence":0.9,"status":"active","trust":"high","originKind":"repoFile","sourceRefs":[{"kind":"lockfile","ref":"Directory.Packages.props","commit":"a1b2c3d"}]},
            {"factId":"f2","kind":"owner","subject":"Pane","value":"charles","confidence":"medium","status":"stale","trust":"agent","originKind":"agent","sourceRefs":[{"kind":"vault","ref":"twin/bazaar/owners.md","commit":null}]}
          ],"pages":[
            {"page":"overview","sentences":[
              {"text":"Bazaar ships a Twin card.","sourceRef":"twin/bazaar/overview.md#L3"},
              {"text":"No source for this line.","sourceRef":null}
            ]}
          ]}
        ]}
        """;

    /// <summary>Builds a pane descriptor for the Twin kind from raw JSON.</summary>
    internal static PaneDescriptor Descriptor(string json) =>
        new(PaneKinds.Twin, "Twin", string.Empty, JsonElement.Parse(json));

    [Fact]
    public void Parses_projects_facts_and_pages()
    {
        var pane = new TwinPaneViewModel(Descriptor(_model));

        Assert.Equal(PaneKinds.Twin, pane.Kind);
        Assert.Equal("Digital Twin", pane.Title);
        var project = Assert.Single(pane.Projects);
        Assert.Equal("bazaar", project.ProjectId);
        Assert.Equal("Bazaar", project.Title);
        Assert.Equal(2, project.Facts.Count);

        var first = project.Facts[0];
        Assert.Equal("dependency: Npgsql = 8.0.0", first.Line);
        Assert.Equal("confidence 0.9 · trust high · origin repoFile", first.Meta);
        Assert.False(first.IsStale);
        Assert.Equal("lockfile Directory.Packages.props @ a1b2c3d", Assert.Single(first.SourceRefs).Text);
    }

    [Fact]
    public void Reads_stale_facts_and_prose_sentences_with_and_without_source()
    {
        var pane = new TwinPaneViewModel(Descriptor(_model));
        var project = pane.Projects[0];

        Assert.True(project.Facts[1].IsStale);
        Assert.Equal("vault twin/bazaar/owners.md", Assert.Single(project.Facts[1].SourceRefs).Text);

        var sentences = Assert.Single(project.Pages).Sentences;
        Assert.True(sentences[0].HasSource);
        Assert.False(sentences[1].HasSource);
    }

    [Fact]
    public void Blank_source_ref_counts_as_missing()
    {
        var pane = new TwinPaneViewModel(Descriptor(
            """{"projects":[{"projectId":"p","title":"P","facts":[],"pages":[{"page":"x","sentences":[{"text":"t","sourceRef":"   "}]}]}]}"""));

        Assert.False(Assert.Single(Assert.Single(pane.Projects).Pages[0].Sentences).HasSource);
    }

    [Fact]
    public void Missing_required_field_raises_pane_model_exception()
    {
        // Every field of a fact in the doc 25 model is required, so dropping any one of them must fail to parse.
        const string fact = """{"factId":"f1","kind":"k","subject":"s","value":"v","confidence":1,"status":"active","trust":"high","originKind":"o","sourceRefs":[]}""";
        foreach (var field in new[] { "factId", "kind", "subject", "value", "confidence", "status", "trust", "originKind", "sourceRefs" })
        {
            var node = JsonNode.Parse(fact)!.AsObject();
            node.Remove(field);
            var model = $$"""{"projects":[{"projectId":"p","title":"P","facts":[{{node.ToJsonString()}}],"pages":[]}]}""";

            var error = Assert.Throws<PaneModelException>(() => new TwinPaneViewModel(Descriptor(model)));
            Assert.Contains(field, error.Message);
        }
    }

    [Fact]
    public void Missing_project_array_raises_pane_model_exception()
    {
        Assert.Throws<PaneModelException>(() => new TwinPaneViewModel(Descriptor("""{"projects":[{"projectId":"p","title":"P","pages":[]}]}""")));
        Assert.Throws<PaneModelException>(() => new TwinPaneViewModel(Descriptor("""{}""")));
    }

    [Fact]
    public void Sentence_without_source_ref_key_raises_pane_model_exception()
    {
        Assert.Throws<PaneModelException>(() => new TwinPaneViewModel(Descriptor(
            """{"projects":[{"projectId":"p","title":"P","facts":[],"pages":[{"page":"x","sentences":[{"text":"t"}]}]}]}""")));
    }

    [Fact]
    public void Unknown_status_raises_pane_model_exception()
    {
        Assert.Throws<PaneModelException>(() => new TwinPaneViewModel(Descriptor(
            """{"projects":[{"projectId":"p","title":"P","facts":[{"factId":"f","kind":"k","subject":"s","value":"v","confidence":1,"status":"maybe","trust":"high","originKind":"o","sourceRefs":[]}],"pages":[]}]}""")));
    }

    [Fact]
    public void Registry_builds_the_twin_card_and_falls_back_on_a_missing_field()
    {
        var registry = PaneRegistry.Compose(PaneViews.Instance, TwinPanes.Modules);
        Assert.Contains(PaneKinds.Twin, registry.Kinds);
        Assert.IsType<TwinPaneViewModel>(registry.Create(Descriptor(_model)));

        var broken = registry.Create(Descriptor("""{"projects":[{"title":"no id","facts":[],"pages":[]}]}"""));
        Assert.IsType<GenericPaneViewModel>(broken);
    }
}
