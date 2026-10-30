using Bazaar.Graph.Build;
using Bazaar.Graph.Model;
using Bazaar.Graph.Query;
using Bazaar.Graph.Storage;
using static Usage;

return Run(args, Console.Out, Console.Error);

static int Run(string[] args, TextWriter output, TextWriter error)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        output.WriteLine(UsageText);
        return 0;
    }

    var (positional, options) = ParseArgs(args.Skip(1).ToArray());
    try
    {
        return args[0] switch
        {
            "build" => Build(positional, options, output),
            "search" => Search(positional, options, output),
            "neighbors" => Neighbors(positional, options, output),
            "gods" => Gods(options, output),
            _ => Fail(error, $"Unknown command '{args[0]}'.\n\n{UsageText}"),
        };
    }
    catch (ArgumentException ex)
    {
        return Fail(error, ex.Message);
    }
    catch (FileNotFoundException ex)
    {
        return Fail(error, ex.Message + " Run 'bazaar-graph build' first.");
    }
    catch (DirectoryNotFoundException ex)
    {
        return Fail(error, ex.Message);
    }
}

static int Build(IReadOnlyList<string> positional, IReadOnlyDictionary<string, string> options, TextWriter output)
{
    var root = Path.GetFullPath(positional.Count > 0 ? positional[0] : ".");
    var outPath = options.GetValueOrDefault("out") ?? Path.Combine(root, ".bazaar-graph", "graph.json");
    var previous = GraphStore.TryLoad(outPath);
    var graph = GraphBuilder.Build(root, previous);
    GraphStore.Save(graph, outPath);

    var cached = previous?.Files.Select(f => (f.Path, f.Sha256)).ToHashSet() ?? [];
    var fromCache = graph.Files.Count(f => cached.Contains((f.Path, f.Sha256)));
    output.WriteLine($"Graph written to {outPath}");
    output.WriteLine($"{graph.Files.Count} files ({graph.Files.Count - fromCache} parsed, {fromCache} from cache), {graph.Nodes.Count} nodes, {graph.Edges.Count} edges.");
    return 0;
}

static int Search(IReadOnlyList<string> positional, IReadOnlyDictionary<string, string> options, TextWriter output)
{
    if (positional.Count == 0)
    {
        throw new ArgumentException("search needs text to look for.");
    }

    var query = new GraphQuery(Load(options));
    foreach (var node in query.Search(string.Join(' ', positional)))
    {
        output.WriteLine($"{node.Id}  [{node.Kind}]");
    }

    return 0;
}

static int Neighbors(IReadOnlyList<string> positional, IReadOnlyDictionary<string, string> options, TextWriter output)
{
    if (positional.Count == 0)
    {
        throw new ArgumentException("neighbors needs a node id or label.");
    }

    var depth = options.TryGetValue("depth", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 1;
    var graph = Load(options);
    var query = new GraphQuery(graph);
    var (node, candidates) = query.Resolve(positional[0]);
    if (node is null)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException($"No node matches '{positional[0]}'.");
        }

        throw new ArgumentException("Several nodes share that label; use an id:\n  " + string.Join("\n  ", candidates.Take(10).Select(c => c.Id)));
    }

    output.WriteLine($"{node.Id}  [{node.Kind}]");
    foreach (var edge in query.Neighbors(node.Id, depth))
    {
        output.WriteLine($"  {edge.Source}  -{edge.Relation.ToString().ToLowerInvariant()}->  {edge.Target}  [{edge.Confidence.ToString().ToUpperInvariant()}]");
    }

    return 0;
}

static int Gods(IReadOnlyDictionary<string, string> options, TextWriter output)
{
    var top = options.TryGetValue("top", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 10;
    foreach (var (node, degree) in new GraphQuery(Load(options)).GodNodes(top))
    {
        output.WriteLine($"{degree,4}  {node.Id}  [{node.Kind}]");
    }

    return 0;
}

static KnowledgeGraph Load(IReadOnlyDictionary<string, string> options)
{
    var path = options.GetValueOrDefault("graph") ?? Path.Combine(".bazaar-graph", "graph.json");
    return GraphStore.TryLoad(path) ?? throw new FileNotFoundException($"No usable graph at {Path.GetFullPath(path)}.");
}

static (List<string> Positional, Dictionary<string, string> Options) ParseArgs(string[] args)
{
    var positional = new List<string>();
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var name = args[i][2..];
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Option --{name} needs a value.");
            }

            options[name] = args[++i];
        }
        else
        {
            positional.Add(args[i]);
        }
    }

    return (positional, options);
}

static int Fail(TextWriter error, string message)
{
    error.WriteLine(message);
    return 1;
}

static class Usage
{
    public const string UsageText = """
        bazaar-graph: a knowledge graph of C# and Markdown sources, for querying instead of reading whole files.

        Commands:
          build [root] [--out <path>]                       build or refresh the graph (default out: <root>/.bazaar-graph/graph.json)
          search <text> [--graph <path>]                    find nodes by label or id
          neighbors <id|label> [--depth N] [--graph <path>] edges within N hops (N = 1..3, default 1)
          gods [--top N] [--graph <path>]                   highest-degree symbols

        --graph defaults to .bazaar-graph/graph.json in the current directory.
        """;
}
