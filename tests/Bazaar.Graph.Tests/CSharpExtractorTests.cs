using Bazaar.Graph.Extraction;
using Bazaar.Graph.Model;

namespace Bazaar.Graph.Tests;

public sealed class CSharpExtractorTests
{
    private const string _source = """
        using System.Text;
        namespace Demo;

        public interface IStore { void Save(); }

        public class Outer : Base, IStore
        {
            public void Save() { Helper(); Log.Write(1); }
            public string Name { get; set; }
            public void Helper() { }

            public class Inner
            {
                public Inner() { Outer.Make<int>(); }
            }
        }
        """;

    private static FileFragment Extract() => CSharpExtractor.Extract("src/Demo.cs", "hash", _source);

    [Fact]
    public void Extracts_types_members_and_nesting()
    {
        var fragment = Extract();
        var ids = fragment.Nodes.Select(n => n.Id).ToHashSet();

        Assert.Contains("src/Demo.cs", ids);
        Assert.Contains("src/Demo.cs#IStore", ids);
        Assert.Contains("src/Demo.cs#Outer", ids);
        Assert.Contains("src/Demo.cs#Outer.Save", ids);
        Assert.Contains("src/Demo.cs#Outer.Name", ids);
        Assert.Contains("src/Demo.cs#Outer.Inner", ids);
        Assert.Contains("src/Demo.cs#Outer.Inner.Inner", ids);
        Assert.Contains(fragment.Edges, e => e.Source == "src/Demo.cs#Outer" && e.Target == "src/Demo.cs#Outer.Save" && e.Relation == Relation.Contains);
    }

    [Fact]
    public void Records_usings_as_imports_with_extracted_confidence()
    {
        var fragment = Extract();

        var edge = Assert.Single(fragment.Edges, e => e.Relation == Relation.Imports);
        Assert.Equal("src/Demo.cs", edge.Source);
        Assert.Equal("ns:System.Text", edge.Target);
        Assert.Equal(Confidence.Extracted, edge.Confidence);
    }

    [Fact]
    public void Records_base_types_and_call_sites_as_pending_refs()
    {
        var fragment = Extract();

        Assert.Contains(fragment.Refs, r => r.Relation == Relation.Inherits && r.Name == "Base" && r.Source == "src/Demo.cs#Outer");
        Assert.Contains(fragment.Refs, r => r.Relation == Relation.Inherits && r.Name == "IStore" && r.Source == "src/Demo.cs#Outer");
        Assert.Contains(fragment.Refs, r => r.Relation == Relation.Calls && r.Name == "Helper" && r.Source == "src/Demo.cs#Outer.Save");
        Assert.Contains(fragment.Refs, r => r.Relation == Relation.Calls && r.Name == "Write" && r.Source == "src/Demo.cs#Outer.Save");
        Assert.Contains(fragment.Refs, r => r.Relation == Relation.Calls && r.Name == "Make" && r.Source == "src/Demo.cs#Outer.Inner.Inner");
    }

    [Fact]
    public void Gives_overloads_unique_ids()
    {
        const string overloads = "class C { void M(int a) { } void M(string s) { } }";

        var fragment = CSharpExtractor.Extract("c.cs", "h", overloads);

        Assert.Contains(fragment.Nodes, n => n.Id == "c.cs#C.M");
        Assert.Contains(fragment.Nodes, n => n.Id == "c.cs#C.M/2");
    }
}
