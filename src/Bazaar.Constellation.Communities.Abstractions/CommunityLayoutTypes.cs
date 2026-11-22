namespace Bazaar.Constellation.Communities.Abstractions;

/// <summary>A node to lay out. <see cref="Label"/> is shown in section names; the id is used when it is null.</summary>
public sealed record CommunityNode(string Id, string? Label = null);

/// <summary>An undirected weighted edge between two node ids. Repeated pairs are summed; zero weight is ignored.</summary>
public sealed record CommunityEdge(string From, string To, double Weight = 1.0);

/// <summary>
/// Operator and developer knobs for the community layout. These are not user graphics settings: the owner decided
/// the Constellation has none, and the render budget is driven by the frame-budget controller.
/// </summary>
public sealed record CommunityLayoutOptions
{
    /// <summary>Modularity resolution (1.0 is standard Louvain). Larger values give more, smaller communities.</summary>
    public double Resolution { get; init; } = 1.0;

    /// <summary>World-space distance between neighbouring nodes inside a community.</summary>
    public float NodeSpacing { get; init; } = 14f;

    /// <summary>Minimum world-space gap between two community discs.</summary>
    public float CommunityGap { get; init; } = 96f;

    /// <summary>
    /// Most sections returned (and so drawn as backdrops). When Louvain finds more communities, the smallest are merged
    /// into one "Other" section. Matches the renderer's per-frame section cap. Zero or less means no cap.
    /// </summary>
    public int MaxSections { get; init; } = 64;
}

/// <summary>
/// One labelled group of nodes. The renderer turns it into a semi-transparent rectangle around its members, so the
/// centre and radius here are layout facts for tests and tools, not draw instructions. <see cref="Color"/> is ARGB.
/// </summary>
public sealed record CommunitySection(
    string Id,
    string Label,
    IReadOnlyList<string> Members,
    uint Color,
    float CenterX,
    float CenterY,
    float Radius);

/// <summary>
/// Positions and sections for one graph. <see cref="Xy"/> holds packed x,y pairs in the order of <see cref="NodeIds"/>
/// (ordinal id order), the same packing as a layout frame.
/// </summary>
public sealed record CommunityLayoutResult(
    IReadOnlyList<string> NodeIds,
    ReadOnlyMemory<float> Xy,
    IReadOnlyList<CommunitySection> Sections,
    double Modularity);
