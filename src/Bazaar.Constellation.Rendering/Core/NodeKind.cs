namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// The closed set of node kinds the renderer understands. The adapter maps its own types onto it; unknown values draw as
/// <see cref="Generic"/>. Doc: docs/design/constellation-node-detail-and-pacing.md section 3 (kind list PROPOSED).
/// </summary>
public enum NodeKind : byte
{
    Generic = 0,
    Document = 1,
    Idea = 2,
    NoteInSpace = 3,
    Label = 4,
    Image = 5,

    /// <summary>Reserved for section frames; not a normal node.</summary>
    Group = 6,
}

/// <summary>Level-of-detail rungs, closest to farthest. Every kind uses the same ladder with kind-specific content per rung.</summary>
public enum NodeDetailLevel : byte
{
    /// <summary>Full: icon, title and text preview (note text in full).</summary>
    L0 = 0,

    /// <summary>Icon and title.</summary>
    L1 = 1,

    /// <summary>Icon only.</summary>
    L2 = 2,

    /// <summary>Square or dot, shape chosen by the theme.</summary>
    L3 = 3,

    /// <summary>Dot, merged into the density field. Identical for every kind.</summary>
    L4 = 4,
}

/// <summary>What a node draws on a rung.</summary>
[Flags]
public enum NodeContent : byte
{
    None = 0,
    Icon = 1,
    Title = 2,

    /// <summary>Body text: a preview for documents, the full text for notes in space.</summary>
    Text = 4,

    /// <summary>Theme-chosen square or dot.</summary>
    Marker = 8,

    /// <summary>Plain dot.</summary>
    Dot = 16,
}
