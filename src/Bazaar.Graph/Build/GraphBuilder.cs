using System.Security.Cryptography;
using System.Text;
using Bazaar.Graph.Extraction;
using Bazaar.Graph.Model;

namespace Bazaar.Graph.Build;

/// <summary>Walks a source tree, extracts each supported file (reusing cached fragments whose SHA256 matches) and resolves pending references into edges.</summary>
public static class GraphBuilder
{
    private static readonly HashSet<string> _skippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".bazaar-graph", "bin", "obj", "node_modules", ".vs", ".idea",
    };

    private const int _ambiguousLimit = 3;

    public static KnowledgeGraph Build(string root, KnowledgeGraph? previous = null)
    {
        var fullRoot = Path.GetFullPath(root);
        var cache = previous?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal)
            ?? new Dictionary<string, FileFragment>(StringComparer.Ordinal);

        var fragments = new List<FileFragment>();
        foreach (var file in EnumerateSources(fullRoot))
        {
            var relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

            if (cache.TryGetValue(relative, out var cached) && cached.Sha256 == hash)
            {
                fragments.Add(cached);
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var fragment = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? CSharpExtractor.Extract(relative, hash, text)
                : MarkdownExtractor.Extract(relative, hash, text);
            fragments.Add(fragment);
        }

        return Resolve(fullRoot, fragments);
    }

    /// <summary>Turns fragments into a resolved graph. Public so tests can resolve hand-made fragments.</summary>
    public static KnowledgeGraph Resolve(string root, IReadOnlyList<FileFragment> fragments)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in fragments.SelectMany(f => f.Nodes))
        {
            nodes.TryAdd(node.Id, node);
        }

        var edges = new List<GraphEdge>();
        var edgeSet = new HashSet<GraphEdge>();
        foreach (var edge in fragments.SelectMany(f => f.Edges))
        {
            if (edgeSet.Add(edge))
            {
                edges.Add(edge);
            }
        }

        var byLabel = nodes.Values
            .Where(n => n.Kind is NodeKind.Type or NodeKind.Member or NodeKind.File)
            .GroupBy(n => n.Label, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var pending in fragments.SelectMany(f => f.Refs))
        {
            if (pending.ByPath)
            {
                ResolvePath(pending, nodes, edges, edgeSet);
            }
            else
            {
                ResolveName(pending, nodes, byLabel, edges, edgeSet);
            }
        }

        var orderedNodes = nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var orderedEdges = edges
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ThenBy(e => e.Relation)
            .ToList();
        var orderedFiles = fragments.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        return new KnowledgeGraph(KnowledgeGraph.CurrentVersion, root, orderedFiles, orderedNodes, orderedEdges);
    }

    private static void ResolvePath(PendingRef pending, Dictionary<string, GraphNode> nodes, List<GraphEdge> edges, HashSet<GraphEdge> edgeSet)
    {
        var sourceDir = Path.GetDirectoryName(pending.File)?.Replace('\\', '/') ?? string.Empty;
        var combined = string.IsNullOrEmpty(sourceDir) ? pending.Name : sourceDir + "/" + pending.Name;
        var target = NormalizePath(combined);
        if (target is not null && nodes.TryGetValue(target, out var node) && node.Kind == NodeKind.File)
        {
            AddEdge(pending.Source, target, pending.Relation, Confidence.Extracted, edges, edgeSet);
        }
    }

    private static void ResolveName(
        PendingRef pending,
        Dictionary<string, GraphNode> nodes,
        Dictionary<string, List<GraphNode>> byLabel,
        List<GraphEdge> edges,
        HashSet<GraphEdge> edgeSet)
    {
        var candidates = byLabel.TryGetValue(pending.Name, out var found)
            ? found.Where(n => n.Id != pending.Source && KindFits(pending.Relation, n.Kind)).ToList()
            : [];

        if (candidates.Count == 0)
        {
            if (pending.Relation == Relation.Inherits)
            {
                var external = "external:" + pending.Name;
                nodes.TryAdd(external, new GraphNode(external, NodeKind.External, pending.Name, string.Empty, 0));
                AddEdge(pending.Source, external, pending.Relation, Confidence.Extracted, edges, edgeSet);
            }

            return;
        }

        if (candidates.Count == 1)
        {
            AddEdge(pending.Source, candidates[0].Id, pending.Relation, Confidence.Inferred, edges, edgeSet);
            return;
        }

        var sameFile = candidates.Where(n => n.File == pending.File).ToList();
        if (sameFile.Count > 0)
        {
            foreach (var node in sameFile)
            {
                AddEdge(pending.Source, node.Id, pending.Relation, Confidence.Inferred, edges, edgeSet);
            }

            return;
        }

        if (candidates.Count <= _ambiguousLimit)
        {
            foreach (var node in candidates)
            {
                AddEdge(pending.Source, node.Id, pending.Relation, Confidence.Ambiguous, edges, edgeSet);
            }
        }

        // Otherwise drop the ref: a common name would flood the graph with guesses.
    }

    private static bool KindFits(Relation relation, NodeKind kind) => relation switch
    {
        Relation.Inherits => kind == NodeKind.Type,
        Relation.Calls => kind == NodeKind.Member,
        Relation.Links => kind == NodeKind.File,
        _ => false,
    };

    private static void AddEdge(string source, string target, Relation relation, Confidence confidence, List<GraphEdge> edges, HashSet<GraphEdge> edgeSet)
    {
        var edge = new GraphEdge(source, target, relation, confidence);
        if (edgeSet.Add(edge))
        {
            edges.Add(edge);
        }
    }

    /// <summary>Collapses <c>.</c> and <c>..</c> segments. Returns null when the path escapes the root.</summary>
    private static string? NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        return string.Join('/', parts);
    }

    private static List<string> EnumerateSources(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var files = new List<string>();
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!_skippedDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) || ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
