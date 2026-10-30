using Bazaar.Graph.Model;
using Microsoft.CodeAnalysis;

namespace Bazaar.Graph.Extraction;

/// <summary>Collects one file's nodes, edges and pending refs, de-duplicating ids as it goes.</summary>
internal sealed class FragmentBuilder
{
    private readonly string _relativePath;
    private readonly string _sha256;
    private readonly List<GraphNode> _nodes = [];
    private readonly List<GraphEdge> _edges = [];
    private readonly List<PendingRef> _refs = [];
    private readonly HashSet<string> _ids = [];
    private readonly HashSet<GraphEdge> _edgeSet = [];

    public FragmentBuilder(string relativePath, string sha256)
    {
        _relativePath = relativePath;
        _sha256 = sha256;
        FileId = relativePath;
    }

    public string FileId { get; }

    public void AddFileNode()
    {
        _ids.Add(FileId);
        _nodes.Add(new GraphNode(FileId, NodeKind.File, Path.GetFileNameWithoutExtension(_relativePath), FileId, 1));
    }

    /// <summary>Adds a symbol node. Overloads and repeats get a <c>/n</c> suffix so ids stay unique.</summary>
    public string AddSymbol(NodeKind kind, string label, string qualified, Location location)
    {
        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        var id = UniqueId(_relativePath + "#" + qualified);
        _nodes.Add(new GraphNode(id, kind, label, _relativePath, line));
        return id;
    }

    /// <summary>Adds a node with a fixed id. Returns the id so callers can link to it.</summary>
    public string AddNode(NodeKind kind, string label, string file, int line, string id)
    {
        if (_ids.Add(id))
        {
            _nodes.Add(new GraphNode(id, kind, label, file, line));
        }

        return id;
    }

    public void AddEdge(string source, string target, Relation relation, Confidence confidence)
    {
        var edge = new GraphEdge(source, target, relation, confidence);
        if (_edgeSet.Add(edge))
        {
            _edges.Add(edge);
        }
    }

    public void AddRef(string source, Relation relation, string name, bool byPath) =>
        _refs.Add(new PendingRef(source, _relativePath, relation, name, byPath));

    public FileFragment Build() => new(_relativePath, _sha256, _nodes, _edges, _refs);

    private string UniqueId(string candidate)
    {
        var id = candidate;
        for (var n = 2; !_ids.Add(id); n++)
        {
            id = candidate + "/" + n;
        }

        return id;
    }
}
