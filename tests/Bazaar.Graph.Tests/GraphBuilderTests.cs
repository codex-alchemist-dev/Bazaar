using Bazaar.Graph.Build;
using Bazaar.Graph.Extraction;
using Bazaar.Graph.Model;
using Bazaar.Graph.Query;
using Bazaar.Graph.Storage;

namespace Bazaar.Graph.Tests;

public sealed class GraphBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-graph-tests-" + Guid.NewGuid().ToString("N"));

    public GraphBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void SeedProject()
    {
        Write("src/Store.cs", "namespace App; public class Store : Repo { public void Save() { Flush(); } public void Flush() { } }");
        Write("src/Repo.cs", "namespace App; public class Repo { }");
        Write("src/Service.cs", "namespace App; public class Service { public void Run(Store s) { s.Save(); } }");
        Write("docs/Guide.md", "# Guide\nSee [[Store]] and [the code](../src/Repo.cs). [[Missing]] is dropped.\n");
        Write("bin/Generated.cs", "class Skipped { }");
    }

    [Fact]
    public void Resolves_inheritance_calls_and_links_with_confidence()
    {
        SeedProject();

        var graph = GraphBuilder.Build(_root);

        Assert.Contains(graph.Edges, e => e.Source == "src/Store.cs#Store" && e.Target == "src/Repo.cs#Repo" && e.Relation == Relation.Inherits && e.Confidence == Confidence.Inferred);
        Assert.Contains(graph.Edges, e => e.Source == "src/Store.cs#Store.Save" && e.Target == "src/Store.cs#Store.Flush" && e.Relation == Relation.Calls && e.Confidence == Confidence.Inferred);
        Assert.Contains(graph.Edges, e => e.Source == "src/Service.cs#Service.Run" && e.Target == "src/Store.cs#Store.Save" && e.Relation == Relation.Calls && e.Confidence == Confidence.Inferred);
        Assert.Contains(graph.Edges, e => e.Source == "docs/Guide.md#guide" && e.Target == "src/Store.cs" && e.Relation == Relation.Links && e.Confidence == Confidence.Inferred);
        Assert.Contains(graph.Edges, e => e.Source == "docs/Guide.md#guide" && e.Target == "src/Repo.cs" && e.Relation == Relation.Links && e.Confidence == Confidence.Extracted);
        Assert.DoesNotContain(graph.Edges, e => e.Target.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Skips_build_output_directories()
    {
        SeedProject();

        var graph = GraphBuilder.Build(_root);

        Assert.DoesNotContain(graph.Files, f => f.Path.StartsWith("bin/", StringComparison.Ordinal));
    }

    [Fact]
    public void Marks_ambiguous_names_across_files_and_drops_crowded_ones()
    {
        Write("a/One.cs", "class A { void Go() { Get(); } void Get() { } }");
        Write("b/Two.cs", "class B { void Get() { } }");
        Write("c/Three.cs", "class C { void Get() { } }");
        Write("d/Four.cs", "class D { void Get() { } }");
        Write("e/Caller.cs", "class E { void Run() { Get(); } }");

        var graph = GraphBuilder.Build(_root);

        // Same-file match wins over other files.
        Assert.Contains(graph.Edges, e => e.Source == "a/One.cs#A.Go" && e.Target == "a/One.cs#A.Get" && e.Confidence == Confidence.Inferred);
        // Four candidates is over the limit, so the cross-file call from E is dropped.
        Assert.DoesNotContain(graph.Edges, e => e.Source == "e/Caller.cs#E.Run");
    }

    [Fact]
    public void Reuses_cached_fragments_for_unchanged_files_and_reparses_edited_ones()
    {
        SeedProject();
        var first = GraphBuilder.Build(_root);
        var cachedStore = first.Files.Single(f => f.Path == "src/Store.cs");

        Write("src/Repo.cs", "namespace App; public class Repo { public void Ping() { } }");
        var second = GraphBuilder.Build(_root, first);

        Assert.Same(cachedStore, second.Files.Single(f => f.Path == "src/Store.cs"));
        Assert.NotEqual(first.Files.Single(f => f.Path == "src/Repo.cs").Sha256, second.Files.Single(f => f.Path == "src/Repo.cs").Sha256);
        Assert.Contains(second.Nodes, n => n.Id == "src/Repo.cs#Repo.Ping");
    }

    [Fact]
    public void Drops_files_that_were_deleted()
    {
        SeedProject();
        var first = GraphBuilder.Build(_root);

        File.Delete(Path.Combine(_root, "src", "Service.cs"));
        var second = GraphBuilder.Build(_root, first);

        Assert.DoesNotContain(second.Files, f => f.Path == "src/Service.cs");
        Assert.DoesNotContain(second.Nodes, n => n.File == "src/Service.cs");
    }

    [Fact]
    public void Store_round_trips_and_rejects_other_versions()
    {
        SeedProject();
        var graph = GraphBuilder.Build(_root);
        var path = Path.Combine(_root, ".bazaar-graph", "graph.json");

        GraphStore.Save(graph, path);
        var loaded = GraphStore.TryLoad(path);

        Assert.NotNull(loaded);
        Assert.Equal(graph.Nodes.Count, loaded.Nodes.Count);
        Assert.Equal(graph.Edges, loaded.Edges);
        Assert.Contains("\"relation\": \"Calls\"", File.ReadAllText(path), StringComparison.Ordinal);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal));
        Assert.Null(GraphStore.TryLoad(path));
    }

    [Fact]
    public void Query_finds_neighbors_and_god_nodes()
    {
        SeedProject();
        var query = new GraphQuery(GraphBuilder.Build(_root));

        var neighbors = query.Neighbors("src/Store.cs#Store.Save", depth: 1);
        Assert.Contains(neighbors, e => e.Target == "src/Store.cs#Store.Flush");
        Assert.Contains(neighbors, e => e.Source == "src/Service.cs#Service.Run");

        var (node, candidates) = query.Resolve("Store");
        Assert.NotNull(node);
        Assert.Single(candidates);

        var gods = query.GodNodes(top: 3);
        Assert.NotEmpty(gods);
        Assert.DoesNotContain(gods, g => g.Node.Kind == NodeKind.File || g.Node.Kind == NodeKind.Section);
    }

    [Fact]
    public void Query_depth_is_capped()
    {
        SeedProject();
        var graph = GraphBuilder.Build(_root);
        var query = new GraphQuery(graph);

        var capped = query.Neighbors("src/Store.cs#Store.Save", depth: 99);
        var atMax = query.Neighbors("src/Store.cs#Store.Save", depth: GraphQuery.MaxDepth);

        Assert.Equal(atMax.Count, capped.Count);
    }

    [Fact]
    public void Resolves_ambiguous_labels_to_candidates()
    {
        Write("x/Dup.cs", "class Dup { }");
        Write("y/Dup.cs", "class Dup { }");
        var query = new GraphQuery(GraphBuilder.Build(_root));

        var (node, candidates) = query.Resolve("Dup");

        Assert.Null(node);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Prefers_symbols_over_files_when_a_label_is_shared()
    {
        Write("src/Store.cs", "class Store { }");
        var query = new GraphQuery(GraphBuilder.Build(_root));

        var (node, candidates) = query.Resolve("Store");

        Assert.Equal("src/Store.cs#Store", node?.Id);
        Assert.Single(candidates);
    }

    [Fact]
    public void Extractors_are_used_per_extension()
    {
        Write("only.md", "# Title\n");
        Write("ignored.txt", "class NotParsed { }");

        var graph = GraphBuilder.Build(_root);

        var file = Assert.Single(graph.Files);
        Assert.Equal("only.md", file.Path);
        Assert.All(graph.Nodes, n => Assert.NotEqual(NodeKind.Type, n.Kind));
    }
}
