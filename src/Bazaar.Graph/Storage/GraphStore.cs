using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Graph.Model;

namespace Bazaar.Graph.Storage;

/// <summary>Reads and writes <c>graph.json</c> with source-generated System.Text.Json (the wire codec used across Bazaar).</summary>
public static class GraphStore
{
    public static void Save(KnowledgeGraph graph, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, graph, GraphJsonContext.Default.KnowledgeGraph);
    }

    /// <summary>Returns null when the file is missing or was written by a different graph version, so the builder starts from scratch.</summary>
    public static KnowledgeGraph? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var graph = JsonSerializer.Deserialize(stream, GraphJsonContext.Default.KnowledgeGraph);
            return graph?.Version == KnowledgeGraph.CurrentVersion ? graph : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(KnowledgeGraph))]
internal sealed partial class GraphJsonContext : JsonSerializerContext
{
}
