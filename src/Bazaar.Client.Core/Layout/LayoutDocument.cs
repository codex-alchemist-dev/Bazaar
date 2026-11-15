using System.Text.Json;

namespace Bazaar.Client.Core.Layout;

/// <summary>
/// A client layout document (doc 41 section 8): which panes appear where, per device class, display and posture.
/// Versioned by <see cref="SchemaVersion"/>; additive changes only within a version (doc 41 section 7).
/// </summary>
/// <param name="SchemaVersion">Document schema version. Currently 1.</param>
/// <param name="Name">Product-facing name of the layout (doc 41 section 10: the term is "Layout").</param>
/// <param name="Locks">Slot patterns the owner has locked for people below the owner. Empty when unlocked.</param>
/// <param name="Slots">One arrangement per slot pattern. Resolved with <see cref="LayoutResolver"/>.</param>
public sealed record LayoutDocument(
    int SchemaVersion,
    string Name,
    IReadOnlyList<string> Locks,
    IReadOnlyList<LayoutSlot> Slots);

/// <summary>
/// One arrangement. <see cref="DeviceClass"/>, <see cref="Display"/> and <see cref="Posture"/> are strings, so an
/// unknown value from a newer server is kept and simply never matches. <c>*</c> matches any value.
/// </summary>
public sealed record LayoutSlot(string DeviceClass, string Display, string Posture, LayoutNode Root);

/// <summary>
/// A node of the layout tree. A container node has <see cref="Container"/> and <see cref="Children"/>; a pane leaf has
/// <see cref="PaneKind"/>. <see cref="PaneConfig"/> is free JSON that the pane's module reads; the host never interprets it.
/// </summary>
public sealed record LayoutNode(
    string? Container,
    IReadOnlyList<LayoutNode> Children,
    string? PaneKind,
    string? PaneId,
    JsonElement? PaneConfig);

/// <summary>One validation problem. Path is a JSON pointer into the document; Code is stable for tests.</summary>
public sealed record LayoutIssue(string Path, string Code, string Message);

/// <summary>Result of loading a layout. Document is null whenever any issue was found.</summary>
public sealed record LayoutLoadResult(LayoutDocument? Document, IReadOnlyList<LayoutIssue> Issues)
{
    public bool IsValid => Document is not null;
}

/// <summary>Well-known values and limits shared by the validator, the resolver and the tree builder.</summary>
public static class LayoutVocabulary
{
    public const int SchemaVersion = 1;

    /// <summary>Wildcard that matches any value in a slot or lock pattern.</summary>
    public const string Any = "*";

    public const int MaxDocumentChars = 256 * 1024;
    public const int MaxNestingDepth = 16;
    public const int MaxPanesPerSlot = 64;
    public const int MaxChildrenPerContainer = 32;
    public const int MaxSlots = 32;

    public static readonly string[] DeviceClasses = ["phone", "foldable", "tablet", "desktop"];
    public static readonly string[] Displays = ["single", "cover", "inner"];
    public static readonly string[] Postures = ["folded", "halfOpen", "unfolded"];
    public static readonly string[] Containers = ["row", "column", "tabs", "stack"];
}
