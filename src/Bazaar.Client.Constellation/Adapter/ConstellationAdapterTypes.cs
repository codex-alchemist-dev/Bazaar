namespace Bazaar.Client.Constellation.Adapter;

/// <summary>
/// What the renderer should draw a node as (constellation-node-detail-and-pacing.md, section 3). A mirror of the doc's closed
/// set: the adapter maps Bazaar document types onto it and never lets a Bazaar word reach the renderer. Unknown types are
/// <see cref="Generic"/>. The doc's <c>Group</c> kind is reserved for section frames and is never produced for a node.
/// </summary>
public enum ConstellationNodeKind
{
    Generic = 0,
    Document = 1,
    Idea = 2,
    NoteInSpace = 3,
    Label = 4,
    Image = 5,
}

/// <summary>
/// One node as the adapter reads it, already parsed from the Vault (or from node metadata for notes without content).
/// <see cref="ContentLinks"/> are the resolved targets of the wiki-links and references written in the body; they are only
/// honoured when <see cref="HasContent"/> is true. <see cref="MetadataLinks"/> are the hidden-metadata connections kept next
/// to a node; they are only honoured for nodes without content, and for sticky notes.
/// </summary>
/// <param name="Id">Graph node id.</param>
/// <param name="Type">The Bazaar type string (front matter <c>type</c>, or a note kind such as <c>sticky</c>).</param>
/// <param name="Title">Display title, used as the label of a group note.</param>
/// <param name="FolderPath">Vault folder of the document, forward slashes, no leading or trailing slash; null when none.</param>
/// <param name="Group">The front-matter <c>group</c> field (or the hidden-metadata group for notes with no content); may be a path such as <c>Parent/Child</c>.</param>
/// <param name="HasContent">True when the node has a body to read links from; false for open notes and label-style nodes.</param>
/// <param name="ContentLinks">Resolved wiki-link and reference targets found in the body, as node ids.</param>
/// <param name="MetadataLinks">Hidden-metadata connections, as node ids.</param>
public sealed record ConstellationSourceNode(
    string Id,
    string Type,
    string? Title = null,
    string? FolderPath = null,
    string? Group = null,
    bool HasContent = true,
    IReadOnlyList<string>? ContentLinks = null,
    IReadOnlyList<string>? MetadataLinks = null);

/// <summary>A directed connection between two node ids, as the renderer's edge input.</summary>
public readonly record struct ConstellationConnection(string From, string To);

/// <summary>
/// Operator and developer knobs for the adapter. Every default is provisional (doc section 8, items 4 to 6) and none is a
/// user-facing quality setting.
/// </summary>
public sealed record ConstellationAdapterSettings
{
    /// <summary>Treat Vault folders as sections.</summary>
    public bool FolderSections { get; init; } = true;

    /// <summary>A folder becomes a section only when it holds at least this many nodes, nested folders included.</summary>
    public int MinFolderMembers { get; init; } = 2;

    /// <summary>Folders deeper than this are folded into their ancestor at this depth.</summary>
    public int MaxFolderDepth { get; init; } = 4;

    /// <summary>Document types whose body lists the members of a section.</summary>
    public IReadOnlySet<string> GroupNoteTypes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "group", "section" };

    /// <summary>Separator that nests a front-matter group value, as in <c>Parent/Child</c>.</summary>
    public char GroupPathSeparator { get; init; } = '/';

    /// <summary>Bazaar type (case-insensitive) to renderer kind. Types not listed are <see cref="ConstellationNodeKind.Generic"/>.</summary>
    public IReadOnlyDictionary<string, ConstellationNodeKind> KindMap { get; init; } = DefaultKindMap;

    /// <summary>Types whose connections come from hidden metadata even if they carry text (sticky notes).</summary>
    public IReadOnlySet<string> MetadataLinkTypes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sticky", "moodboard-note" };

    /// <summary>Section colour passed to the renderer (the theme may recolour it).</summary>
    public uint SectionColor { get; init; } = 0xFFFFFFFFu;

    public static IReadOnlyDictionary<string, ConstellationNodeKind> DefaultKindMap { get; } =
        new Dictionary<string, ConstellationNodeKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["index"] = ConstellationNodeKind.Document,
            ["plan"] = ConstellationNodeKind.Document,
            ["deep-dive"] = ConstellationNodeKind.Document,
            ["research"] = ConstellationNodeKind.Document,
            ["constitution"] = ConstellationNodeKind.Document,
            ["decision"] = ConstellationNodeKind.Document,
            ["archive"] = ConstellationNodeKind.Document,
            ["memory"] = ConstellationNodeKind.Document,
            ["summary"] = ConstellationNodeKind.Document,
            ["twin"] = ConstellationNodeKind.Document,
            ["charter"] = ConstellationNodeKind.Document,
            ["draft"] = ConstellationNodeKind.Document,
            ["group"] = ConstellationNodeKind.Document,
            ["section"] = ConstellationNodeKind.Document,
            ["idea"] = ConstellationNodeKind.Idea,
            ["sticky"] = ConstellationNodeKind.NoteInSpace,
            ["moodboard-note"] = ConstellationNodeKind.NoteInSpace,
            ["note"] = ConstellationNodeKind.NoteInSpace,
            ["label"] = ConstellationNodeKind.Label,
            ["image"] = ConstellationNodeKind.Image,
        };
}
