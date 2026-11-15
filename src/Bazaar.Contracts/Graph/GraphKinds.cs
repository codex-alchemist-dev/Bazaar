namespace Bazaar.Contracts.Graph;

/// <summary>
/// The kind of a graph node (doc 19, node kinds). <see cref="Unspecified"/> is the default for nodes that do not carry a kind yet,
/// so existing producers keep their wire shape. Clients draw by kind and never parse id prefixes.
/// </summary>
public enum GraphNodeKind
{
    Unspecified = 0,
    Objective = 1,
    Phase = 2,
    /// <summary>A Vault document (doc 19: Vault nodes).</summary>
    Vault = 3,
}

/// <summary>
/// The kind of a directed graph edge (doc 19, edge kinds). <see cref="Unspecified"/> is the default for edges that do not carry a kind yet.
/// </summary>
public enum GraphEdgeKind
{
    Unspecified = 0,
    /// <summary>An objective depends on another objective (<c>depends_on</c> in the front matter).</summary>
    DependsOn = 1,
    /// <summary>An objective belongs to a phase (the <c>phase</c> front-matter key).</summary>
    PhaseMembership = 2,
    /// <summary>A resolved [[wiki-link]] between two Vault documents.</summary>
    WikiLink = 3,
}
