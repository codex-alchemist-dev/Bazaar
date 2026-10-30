using System.Text.Json.Serialization;

namespace Bazaar.Graph.Model;

[JsonConverter(typeof(JsonStringEnumConverter<NodeKind>))]
public enum NodeKind
{
    File,
    Namespace,
    Type,
    Member,
    Section,
    External,
}

[JsonConverter(typeof(JsonStringEnumConverter<Relation>))]
public enum Relation
{
    Contains,
    Imports,
    Inherits,
    Calls,
    Links,
}

[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    Extracted,
    Inferred,
    Ambiguous,
}

public sealed record GraphNode(string Id, NodeKind Kind, string Label, string File, int Line);

public sealed record GraphEdge(string Source, string Target, Relation Relation, Confidence Confidence);

/// <summary>
/// A reference that the per-file extractor cannot resolve on its own. The builder
/// resolves it after every file has been read. <see cref="ByPath"/> means
/// <see cref="Name"/> is a path relative to <see cref="File"/>.
/// </summary>
public sealed record PendingRef(string Source, string File, Relation Relation, string Name, bool ByPath);

/// <summary>One source file's extraction result. This is also its cache entry.</summary>
public sealed record FileFragment(
    string Path,
    string Sha256,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<PendingRef> Refs);

/// <summary>The whole graph. <see cref="Files"/> is the incremental cache; <see cref="Nodes"/> and <see cref="Edges"/> are derived from it.</summary>
public sealed record KnowledgeGraph(
    int Version,
    string Root,
    IReadOnlyList<FileFragment> Files,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges)
{
    public const int CurrentVersion = 1;
}
